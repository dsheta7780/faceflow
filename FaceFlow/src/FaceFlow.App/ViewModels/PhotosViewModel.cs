using System.Collections.ObjectModel;
using System.Windows;
using FaceFlow.App.Mvvm;
using FaceFlow.Core.Export;
using Microsoft.Win32;

namespace FaceFlow.App.ViewModels;

public enum PhotoFilter { All, NoFaces }

public sealed class PhotosViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly PhotoFilter _filter;
    private string _search = "";
    private int _page;
    private bool _busy;
    private const int PageSize = 120;

    public ObservableCollection<PhotoTile> Photos { get; } = new();

    public string Title => _filter == PhotoFilter.NoFaces ? "No Faces" : "All Photos";
    public string Subtitle => _filter == PhotoFilter.NoFaces
        ? "Photos that were scanned successfully but contain no detectable face."
        : "Everything FaceFlow has indexed.";
    public bool IsNoFaces => _filter == PhotoFilter.NoFaces;
    public string Search { get => _search; set { if (Set(ref _search, value)) { _page = 0; Refresh(); } } }
    public string PageText => $"Page {_page + 1}";
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public bool IsEmpty => Photos.Count == 0;

    public RelayCommand NextPage { get; }
    public RelayCommand PrevPage { get; }
    public RelayCommand OpenInExplorer { get; }
    public AsyncRelayCommand ExportNoFaces { get; }

    public PhotosViewModel(MainViewModel main, PhotoFilter filter)
    {
        _main = main;
        _filter = filter;

        NextPage = new RelayCommand(() => { _page++; Refresh(); }, () => Photos.Count >= PageSize);
        PrevPage = new RelayCommand(() => { if (_page > 0) { _page--; Refresh(); } }, () => _page > 0);

        OpenInExplorer = new RelayCommand(p =>
        {
            if (p is not PhotoTile t) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{t.Path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Core.Log.Warn("Explorer open failed: " + ex.Message); }
        });

        ExportNoFaces = new AsyncRelayCommand(async () =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose where to create the 'No Faces' folder" };
            if (dlg.ShowDialog() != true) return;

            var paths = _main.Repo.GetNoFacePhotoPaths();
            var confirm = MessageBox.Show(
                $"Copy {paths.Count:N0} photo(s) with no detected faces into:\n{dlg.FolderName}\\No Faces\n\n" +
                "Originals are read only — nothing is moved, renamed, resized or deleted.",
                "Create No Faces folder", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirm != MessageBoxResult.OK) return;

            Busy = true;
            try
            {
                var roots = _main.Repo.GetLibraries().Select(l => l.Path).ToList();
                var result = await Task.Run(() =>
                    FolderExporter.Export(paths, dlg.FolderName, "No Faces", ExportMode.Copy, roots));
                _main.Status = $"Created {result.Destination} — {result.Written:N0} copied.";
                MessageBox.Show($"Folder created:\n{result.Destination}\n\nCopied {result.Written:N0}, failed {result.Failed:N0}.",
                                "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { Busy = false; }
        }, () => _filter == PhotoFilter.NoFaces);
    }

    public void Refresh()
    {
        var rows = _main.Repo.GetPhotos(PageSize, _page * PageSize,
                                        _filter == PhotoFilter.NoFaces ? 0 : null,
                                        string.IsNullOrWhiteSpace(Search) ? null : Search.Trim());
        Photos.Clear();
        foreach (var r in rows)
            Photos.Add(new PhotoTile { PhotoId = r.Id, Path = r.Path, FaceCount = r.FaceCount });

        Raise(nameof(PageText)); Raise(nameof(IsEmpty));

        var snapshot = Photos.ToList();
        _ = Task.Run(() =>
        {
            foreach (var t in snapshot)
            {
                var img = Thumbs.Load(t.Path, 240);
                Application.Current?.Dispatcher.BeginInvoke(() => t.Thumb = img);
            }
        });
    }
}
