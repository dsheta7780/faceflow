using System.Collections.Concurrent;
using System.Diagnostics;
using FaceFlow.Core.Clustering;
using FaceFlow.Core.Data;
using FaceFlow.Core.Faces;
using FaceFlow.Core.Imaging;

namespace FaceFlow.Core.Scanning;

public sealed class ScanSettings
{
    public int Workers = Math.Max(2, Environment.ProcessorCount - 2);
    public int DecodeMaxDimension = 1600;   // long edge used for detection + crops
    public int ThumbnailSize = 192;
    public bool PreferGpu = true;
    public bool GenerateThumbnails = true;
    public int BatchSize = 256;             // pending photos claimed per round
}

/// <summary>
/// The scanning pipeline.
///
///   enumerate files ─► fingerprint compare (skip unchanged) ─► pending queue in SQLite
///        │
///        └─► N parallel workers: decode ► detect ► align ► embed ► face thumbnail
///                 │
///                 └─► ONE serialised consumer: write to SQLite + cluster
///
/// The serialised consumer is deliberate. It keeps the centroids and the database
/// consistent without lock contention, and it is never the bottleneck because the AI
/// work upstream is orders of magnitude more expensive.
/// </summary>
public sealed class ScanEngine : IDisposable
{
    private readonly Repository _repo;
    private readonly ScanSettings _settings;
    private FaceEngine? _engine;
    private CancellationTokenSource? _cts;
    private volatile bool _paused;

    public event Action<ScanProgress>? ProgressChanged;
    public bool IsRunning { get; private set; }
    public ScanProgress Progress { get; } = new();

    public ScanEngine(Repository repo, ScanSettings? settings = null)
    {
        _repo = repo;
        _settings = settings ?? new ScanSettings();
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public bool IsPaused => _paused;
    public void Cancel() => _cts?.Cancel();

    private void Report() => ProgressChanged?.Invoke(Progress);

    public async Task ScanAsync(IReadOnlyList<LibraryRow> libraries, CancellationToken external = default)
    {
        if (IsRunning) return;
        IsRunning = true;
        _paused = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            EnsureEngine();
            Progress.Gpu = _engine!.UsingGpu;

            // ---------------------------------------------------- phase 1: index
            Progress.Phase = ScanPhase.Enumerating;
            Progress.FilesSeen = Progress.FilesQueued = Progress.FilesSkipped = 0;
            Progress.Message = "Building file index...";
            Report();

            foreach (var lib in libraries)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(lib.Path))
                {
                    Log.Warn($"Library path missing, skipped: {lib.Path}");
                    continue;
                }

                await Task.Run(() =>
                {
                    var known = _repo.GetPhotoFingerprints(lib.Id);
                    var batch = new List<(string, long, long)>(4096);

                    foreach (var f in FileWalker.Walk(lib.Path, ct, seen =>
                             {
                                 Progress.FilesSeen = seen;
                                 Report();
                             }))
                    {
                        if (known.TryGetValue(f.Path, out var fp)
                            && fp.Size == f.Size && fp.MTime == f.MTime && fp.State == (int)PhotoState.Indexed)
                        {
                            Progress.FilesSkipped++;
                            continue;
                        }

                        batch.Add(f);
                        if (batch.Count >= 4096)
                        {
                            Progress.FilesQueued += _repo.UpsertPendingPhotos(lib.Id, batch);
                            batch.Clear();
                            Report();
                        }
                    }

                    if (batch.Count > 0)
                        Progress.FilesQueued += _repo.UpsertPendingPhotos(lib.Id, batch);
                }, ct);
            }

            // ---------------------------------------------- phase 2: AI processing
            Progress.Phase = ScanPhase.Processing;
            Progress.Processed = 0;
            Progress.Total = libraries.Sum(l => _repo.CountPending(l.Id));
            Progress.Message = Progress.Total == 0
                ? "Everything is already indexed."
                : $"{Progress.Total:N0} photos to process";
            Report();

            var clusterer = new IncrementalClusterer(_repo);
            Progress.PeopleKnown = clusterer.PersonCount;

            foreach (var lib in libraries)
            {
                while (!ct.IsCancellationRequested)
                {
                    var pending = _repo.TakePendingPhotos(lib.Id, _settings.BatchSize);
                    if (pending.Count == 0) break;
                    await ProcessBatchAsync(pending, clusterer, sw, ct);
                }
                _repo.TouchLibraryScan(lib.Id);
            }

            Progress.Phase = ScanPhase.Finishing;
            Progress.Message = "Finalising index...";
            Report();
            _repo.Database.Checkpoint();

            Progress.Phase = ScanPhase.Completed;
            Progress.Message = $"Done. {Progress.Processed:N0} photos processed, " +
                               $"{Progress.FilesSkipped:N0} skipped as unchanged.";
        }
        catch (OperationCanceledException)
        {
            Progress.Phase = ScanPhase.Cancelled;
            Progress.Message = "Scan stopped. Progress is saved — the next scan resumes here.";
        }
        catch (Exception ex)
        {
            Progress.Phase = ScanPhase.Failed;
            Progress.Message = ex.Message;
            Log.Error("Scan failed", ex);
        }
        finally
        {
            Progress.Elapsed = sw.Elapsed;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            Report();
        }
    }

    private sealed class WorkItem
    {
        public long PhotoId;
        public string Path = "";
        public int Width, Height;
        public List<DetectedFace> Faces = new();
        public RgbImage? Image;
        public string? Error;
    }

    private async Task ProcessBatchAsync(List<PhotoRow> pending, IncrementalClusterer clusterer,
                                         Stopwatch sw, CancellationToken ct)
    {
        var queue = new BlockingCollection<WorkItem>(_settings.Workers * 4);

        // -------- consumer: single-threaded DB writer + clusterer
        var consumer = Task.Run(() =>
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    var rows = new List<FaceRow>(item.Faces.Count);
                    var decisions = new List<(IncrementalClusterer.Assignment A, float[] E)>(item.Faces.Count);

                    foreach (var f in item.Faces)
                    {
                        if (f.Embedding is null) continue;
                        var a = clusterer.Classify(f.Embedding, f.Quality);
                        decisions.Add((a, f.Embedding));
                        rows.Add(new FaceRow
                        {
                            X = f.X, Y = f.Y, W = f.W, H = f.H,
                            DetScore = f.Score,
                            Quality = f.Quality,
                            Similarity = a.Similarity,
                            Status = a.Status,
                            PersonId = a.CreatedNewPerson ? null : a.PersonId,
                            EmbeddingRef = f.Embedding
                        });
                    }

                    var faceIds = _repo.SavePhotoResult(item.PhotoId, item.Width, item.Height, rows, item.Error);

                    for (int i = 0; i < faceIds.Count && i < decisions.Count; i++)
                    {
                        var (a, emb) = decisions[i];
                        var faceId = faceIds[i];

                        if (a.Status == FaceStatus.Ignored) continue;

                        if (a.CreatedNewPerson)
                        {
                            var pid = clusterer.CreateCluster(emb, faceId);
                            _repo.SetFaceStatus(faceId, FaceStatus.Assigned, pid);
                        }
                        else if (a.Status == FaceStatus.Assigned && a.PersonId is long existing)
                        {
                            clusterer.Absorb(existing, emb);
                        }
                        // NeedsReview faces stay attached to the suggested person but are
                        // NOT absorbed into the centroid until a human confirms them.

                        if (_settings.GenerateThumbnails && item.Image is not null)
                        {
                            try
                            {
                                var face = item.Faces[i];
                                item.Image.SaveCropJpeg(AppPaths.FaceThumbPath(faceId),
                                    face.X, face.Y, face.W, face.H, _settings.ThumbnailSize);
                            }
                            catch (Exception ex) { Log.Warn($"Thumbnail failed for face {faceId}: {ex.Message}"); }
                        }
                    }

                    Progress.FacesFound += rows.Count;
                    if (item.Error is not null) Progress.Failures++;
                }
                catch (Exception ex)
                {
                    Progress.Failures++;
                    Log.Error($"Consumer failed on {item.Path}", ex);
                    try { _repo.SavePhotoResult(item.PhotoId, 0, 0, Array.Empty<FaceRow>(), ex.Message); }
                    catch { }
                }
                finally
                {
                    item.Image = null;   // release the decoded bitmap promptly
                    Progress.Processed++;
                    Progress.PeopleKnown = clusterer.PersonCount;
                    Progress.Elapsed = sw.Elapsed;
                    var secs = sw.Elapsed.TotalSeconds;
                    Progress.PhotosPerSecond = secs > 0.5 ? Progress.Processed / secs : 0;
                    if (Progress.PhotosPerSecond > 0.01 && Progress.Total > Progress.Processed)
                        Progress.Eta = TimeSpan.FromSeconds((Progress.Total - Progress.Processed) / Progress.PhotosPerSecond);
                    if (Progress.Processed % 8 == 0) Report();
                }
            }
        }, CancellationToken.None);

        // -------- producers: decode + detect + embed
        try
        {
            await Parallel.ForEachAsync(pending,
                new ParallelOptions { MaxDegreeOfParallelism = _settings.Workers, CancellationToken = ct },
                async (photo, token) =>
                {
                    while (_paused && !token.IsCancellationRequested)
                        await Task.Delay(200, token);
                    token.ThrowIfCancellationRequested();

                    var item = new WorkItem { PhotoId = photo.Id, Path = photo.Path };
                    Progress.CurrentFile = photo.Path;

                    try
                    {
                        var img = RgbImage.Load(photo.Path, _settings.DecodeMaxDimension);
                        item.Width = img.OriginalWidth;
                        item.Height = img.OriginalHeight;
                        item.Image = img;

                        var faces = _engine!.Detect(img);
                        foreach (var f in faces)
                        {
                            token.ThrowIfCancellationRequested();
                            try { f.Embedding = _engine.Embed(img, f); }
                            catch (Exception ex) { Log.Warn($"Embedding failed in {photo.Path}: {ex.Message}"); }
                        }
                        item.Faces = faces.Where(f => f.Embedding is not null).ToList();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        item.Error = ex.Message;
                        item.Image = null;
                        Log.Warn($"Could not process '{photo.Path}': {ex.Message}");
                    }

                    queue.Add(item, CancellationToken.None);
                });
        }
        finally
        {
            queue.CompleteAdding();
            await consumer;
            queue.Dispose();
            Report();
        }
    }

    private void EnsureEngine()
    {
        _engine ??= FaceEngine.Create(new FaceEngineOptions { PreferGpu = _settings.PreferGpu });
    }

    public FaceEngine GetEngine()
    {
        EnsureEngine();
        return _engine!;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _engine?.Dispose();
    }
}
