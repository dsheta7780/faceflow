using FaceFlow.Core.Imaging;

namespace FaceFlow.Core.Scanning;

/// <summary>
/// Iterative directory walk. Uses an explicit stack (no recursion, no stack overflow on deep
/// trees) and swallows per-directory access errors so one locked folder can't kill a scan
/// across a million-file library.
/// </summary>
public static class FileWalker
{
    public static IEnumerable<(string Path, long Size, long MTime)> Walk(
        string root, CancellationToken ct, Action<long>? onSeen = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        long seen = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> subdirs = Array.Empty<string>();
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException ex) { Log.Warn($"Skipping '{dir}': {ex.Message}"); }

            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                // Skip our own output and obvious system noise.
                if (name.StartsWith('.') || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(d);
            }

            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ex) { Log.Warn($"Skipping files in '{dir}': {ex.Message}"); continue; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!RgbImage.IsSupported(f)) continue;

                long size, mtime;
                try
                {
                    var fi = new FileInfo(f);
                    size = fi.Length;
                    mtime = fi.LastWriteTimeUtc.Ticks;
                }
                catch { continue; }

                if (++seen % 2000 == 0) onSeen?.Invoke(seen);
                yield return (f, size, mtime);
            }
        }
        onSeen?.Invoke(seen);
    }
}
