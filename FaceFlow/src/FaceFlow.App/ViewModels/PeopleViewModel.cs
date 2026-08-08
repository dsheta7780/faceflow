using System.Collections.ObjectModel;
using System.Windows;
using FaceFlow.App.Mvvm;
using FaceFlow.Core.Data;
using FaceFlow.Core.Export;
using Microsoft.Win32;

namespace FaceFlow.App.ViewModels;

public sealed class PeopleViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _search = "";
    private PersonTile? _selected;
    private bool _showDetail;
    private string _renameText = "";
    private string _detailTitle = "";
    private bool _busy;
    private string _busyText = "";

    public ObservableCollection<PersonTile> People { get; } = new();
    public ObservableCollection<PhotoTile> DetailPhotos { get; } = new();
    public ObservableCollection<PersonTile> MergeCandidates { get; } = new();

    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    public PersonTile? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool ShowDetail { get => _showDetail; set { Set(ref _showDetail, value); Raise(nameof(ShowGallery)); } }
    public bool ShowGallery => !ShowDetail;
    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }
    public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }
    public string CountText => People.Count == 1 ? "1 person" : $"{People.Count:N0} people";

    public RelayCommand Open { get; }
    public RelayCommand Back { get; }
    public RelayCommand Rename { get; }
    public AsyncRelayCommand CreateFolder { get; }
    public RelayCommand Merge { get; }
    public RelayCommand SetCover { get; }
    public RelayCommand RemoveFace { get; }
    public RelayCommand SplitSelected { get; }
    public RelayCommand OpenInExplorer { get; }
    public RelayCommand DeletePerson { get; }

    public PeopleViewModel(MainViewModel main)
    {
        _main = main;

        Open = new RelayCommand(p => { if (p is PersonTile t) OpenPerson(t.Id); });

        Back = new RelayCommand(() => { ShowDetail = false; Refresh(); });

        Rename = new RelayCommand(() =>
        {
            if (Selected is null || string.IsNullOrWhiteSpace(RenameText)) return;
            _main.Repo.RenamePerson(Selected.Id, RenameText);
            Selected.Name = RenameText.Trim();
            DetailTitle = Selected.Name;
            _main.Status = $"Renamed to {Selected.Name}. Future photos of this person will be matched automatically.";
        }, () => Selected is not null && !string.IsNullOrWhiteSpace(RenameText));

        CreateFolder = new AsyncRelayCommand(async () =>
        {
            if (Selected is null) return;
            var dlg = new OpenFolderDialog { Title = "Choose where to create the folder" };
            if (dlg.ShowDialog() != true) return;

            var paths = _main.Repo.GetPersonPhotoPaths(Selected.Id);
            var name = Selected.Name;

            var confirm = MessageBox.Show(
                $"Create '{name}' in:\n{dlg.FolderName}\n\n" +
                $"{paths.Count:N0} original photo(s) will be COPIED at full resolution.\n\n" +
                "Your source photos are opened read-only. Nothing is resized, re-encoded, " +
                "moved, renamed or deleted.",
                "Create folder", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirm != MessageBoxResult.OK) return;

            Busy = true;
            var roots = _main.Repo.GetLibraries().Select(l => l.Path).ToList();
            try
            {
                var progress = new Progress<(int Done, int Total)>(t =>
                    BusyText = $"Copying {t.Done:N0} of {t.Total:N0}...");

                var result = await Task.Run(() =>
                    FolderExporter.Export(paths, dlg.FolderName, name,
                                          ExportMode.Copy, roots, progress));

                _main.Status = $"Created {result.Destination} — {result.Written:N0} copied, " +
                               $"{result.Failed:N0} failed.";
                MessageBox.Show(
                    $"Folder created:\n{result.Destination}\n\n" +
                    $"Copied: {result.Written:N0}\nSkipped: {result.Skipped:N0}\nFailed: {result.Failed:N0}" +
                    (result.Errors.Count > 0 ? "\n\nFirst problems:\n" + string.Join("\n", result.Errors.Take(5)) : ""),
                    "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { Busy = false; BusyText = ""; }
        }, () => Selected is not null);

        Merge = new RelayCommand(p =>
        {
            if (Selected is null || p is not PersonTile other || other.Id == Selected.Id) return;
            var answer = MessageBox.Show(
                $"Merge '{other.Name}' into '{Selected.Name}'?\n\n" +
                $"{other.FaceCount:N0} face(s) will move across. This only changes FaceFlow's index.",
                "Merge people", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            _main.Repo.MergePeople(Selected.Id, other.Id);
            _main.Status = $"Merged {other.Name} into {Selected.Name}.";
            OpenPerson(Selected.Id);
        }, p => Selected is not null && p is PersonTile);

        SetCover = new RelayCommand(p =>
        {
            if (Selected is null || p is not PhotoTile t || t.FaceId is not long fid) return;
            _main.Repo.SetCoverFace(Selected.Id, fid);
            _main.Status = "Cover photo updated.";
        });

        RemoveFace = new RelayCommand(p =>
        {
            if (p is not PhotoTile t || t.FaceId is not long fid) return;
            _main.Repo.RejectFace(fid);
            DetailPhotos.Remove(t);
            if (Selected is not null) _main.Repo.RecomputeCentroid(Selected.Id);
            _main.Status = "Face removed from this person. The original photo is untouched.";
        });

        SplitSelected = new RelayCommand(() =>
        {
            var chosen = DetailPhotos.Where(t => t.IsSelected && t.FaceId is not null)
                                     .Select(t => t.FaceId!.Value).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show("Select one or more photos first (click the checkbox on a tile).",
                                "Split", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var name = Prompt.Ask("Name for the new person", "Split into new person", "New person");
            if (string.IsNullOrWhiteSpace(name)) return;

            var id = _main.Repo.SplitFacesToNewPerson(chosen, name);
            if (Selected is not null) _main.Repo.RecomputeCentroid(Selected.Id);
            _main.Status = $"Moved {chosen.Count:N0} face(s) into '{name}'.";
            OpenPerson(id);
        });

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

        DeletePerson = new RelayCommand(() =>
        {
            if (Selected is null) return;
            var answer = MessageBox.Show(
                $"Delete the person '{Selected.Name}' from FaceFlow?\n\n" +
                "Their faces become unassigned. No photo files are affected.",
                "Delete person", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
            _main.Repo.DeletePerson(Selected.Id);
            ShowDetail = false;
            Refresh();
        }, () => Selected is not null);
    }

    public void Refresh()
    {
        var rows = _main.Repo.GetPeople(string.IsNullOrWhiteSpace(Search) ? null : Search.Trim());
        People.Clear();
        foreach (var r in rows) People.Add(PersonTile.From(r));
        Raise(nameof(CountText));
        _ = LoadThumbsAsync(People.ToList());
    }

    public void OpenPerson(long id)
    {
        var row = _main.Repo.GetPeople().FirstOrDefault(p => p.Id == id);
        if (row is null) return;

        Selected = PersonTile.From(row);
        Selected.LoadThumb();
        RenameText = row.Name;
        DetailTitle = row.Name;
        ShowDetail = true;

        DetailPhotos.Clear();
        foreach (var f in _main.Repo.GetPersonFaces(id, 600))
            DetailPhotos.Add(new PhotoTile
            {
                PhotoId = f.PhotoId, FaceId = f.Id, Path = f.PhotoPath,
                Similarity = f.Similarity, Status = f.Status
            });

        MergeCandidates.Clear();
        foreach (var p in _main.Repo.GetPeople().Where(p => p.Id != id).Take(60))
            MergeCandidates.Add(PersonTile.From(p));

        _ = LoadPhotoThumbsAsync(DetailPhotos.ToList());
        _ = LoadThumbsAsync(MergeCandidates.ToList());
    }

    private static async Task LoadThumbsAsync(List<PersonTile> tiles)
    {
        await Task.Run(() =>
        {
            foreach (var t in tiles)
            {
                var img = t.CoverFaceId is long id ? Thumbs.Face(id, 200) : null;
                Application.Current?.Dispatcher.BeginInvoke(() => t.Thumb = img);
            }
        });
    }

    private static async Task LoadPhotoThumbsAsync(List<PhotoTile> tiles)
    {
        await Task.Run(() =>
        {
            foreach (var t in tiles)
            {
                var img = t.FaceId is long fid ? Thumbs.Face(fid, 220) : Thumbs.Load(t.Path, 220);
                Application.Current?.Dispatcher.BeginInvoke(() => t.Thumb = img);
            }
        });
    }
}
