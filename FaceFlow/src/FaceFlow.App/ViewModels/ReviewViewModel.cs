using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using FaceFlow.App.Mvvm;
using FaceFlow.Core.Data;

namespace FaceFlow.App.ViewModels;

/// <summary>
/// The Review workspace: borderline matches the clusterer was not confident about.
/// Nothing here is absorbed into a person's face signature until you confirm it,
/// so one wrong guess never poisons future matching.
/// </summary>
public sealed class ReviewViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _index;
    private ImageSource? _photo;
    private ImageSource? _faceCrop;

    public ObservableCollection<FaceRow> Queue { get; } = new();

    public FaceRow? CurrentFace => _index >= 0 && _index < Queue.Count ? Queue[_index] : null;
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value); }
    public ImageSource? FaceCrop { get => _faceCrop; set => Set(ref _faceCrop, value); }

    public bool HasWork => Queue.Count > 0;
    public bool IsEmpty => Queue.Count == 0;
    public string PositionText => Queue.Count == 0 ? "Nothing to review" : $"{_index + 1} of {Queue.Count:N0}";
    public string SuggestionText => CurrentFace?.PersonName is { } n ? n : "No suggestion";
    public string ConfidenceText => CurrentFace is null ? "" : $"{CurrentFace.Similarity * 100:0}% match";
    public string FileText => CurrentFace is null ? "" : System.IO.Path.GetFileName(CurrentFace.PhotoPath);
    public string FolderText => CurrentFace is null ? "" : System.IO.Path.GetDirectoryName(CurrentFace.PhotoPath) ?? "";

    public RelayCommand Accept { get; }
    public RelayCommand Reject { get; }
    public RelayCommand MakeNewPerson { get; }
    public RelayCommand AssignTo { get; }
    public RelayCommand Next { get; }
    public RelayCommand Previous { get; }
    public RelayCommand Skip { get; }

    public ReviewViewModel(MainViewModel main)
    {
        _main = main;

        Accept = new RelayCommand(() =>
        {
            if (CurrentFace is not { PersonId: long pid } f) return;
            _main.Repo.ConfirmFace(f.Id);
            _main.Repo.RecomputeCentroid(pid);
            _main.Status = $"Confirmed as {f.PersonName}.";
            RemoveCurrent();
        }, () => CurrentFace?.PersonId is not null);

        Reject = new RelayCommand(() =>
        {
            if (CurrentFace is not { } f) return;
            var old = f.PersonId;
            _main.Repo.RejectFace(f.Id);
            if (old is long pid) _main.Repo.RecomputeCentroid(pid);
            _main.Status = "Rejected. The photo file itself is untouched.";
            RemoveCurrent();
        }, () => CurrentFace is not null);

        MakeNewPerson = new RelayCommand(() =>
        {
            if (CurrentFace is not { } f) return;
            var name = Prompt.Ask("Who is this?", "New person", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            _main.Repo.SplitFacesToNewPerson(new[] { f.Id }, name);
            _main.Status = $"Created '{name}' from this face.";
            RemoveCurrent();
        }, () => CurrentFace is not null);

        AssignTo = new RelayCommand(() =>
        {
            if (CurrentFace is not { } f) return;
            var people = _main.Repo.GetPeople();
            var name = Prompt.Ask(
                "Type an existing person's name:\n\n" +
                string.Join("   ·   ", people.Where(p => p.IsNamed).Take(20).Select(p => p.Name)),
                "Assign to person", f.PersonName ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;

            var target = people.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                MessageBox.Show($"No person called '{name}'. Use 'New person' to create one.",
                                "Assign", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _main.Repo.SetFaceStatus(f.Id, FaceStatus.Confirmed, target.Id);
            _main.Repo.RecomputeCentroid(target.Id);
            _main.Status = $"Assigned to {target.Name}.";
            RemoveCurrent();
        }, () => CurrentFace is not null);

        Next = new RelayCommand(() => Move(1), () => _index < Queue.Count - 1);
        Previous = new RelayCommand(() => Move(-1), () => _index > 0);
        Skip = new RelayCommand(() => Move(1), () => Queue.Count > 1);
    }

    public void Refresh()
    {
        Queue.Clear();
        foreach (var f in _main.Repo.GetReviewQueue(300)) Queue.Add(f);
        _index = 0;
        LoadCurrent();
        RaiseAll();
    }

    private void RemoveCurrent()
    {
        if (_index < 0 || _index >= Queue.Count) return;
        Queue.RemoveAt(_index);
        if (_index >= Queue.Count) _index = Queue.Count - 1;
        if (_index < 0) _index = 0;
        LoadCurrent();
        RaiseAll();
        _main.RefreshCounters();
    }

    private void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, Math.Max(0, Queue.Count - 1));
        LoadCurrent();
        RaiseAll();
    }

    private void LoadCurrent()
    {
        var f = CurrentFace;
        if (f is null) { Photo = null; FaceCrop = null; return; }

        FaceCrop = Thumbs.Face(f.Id, 220);
        var path = f.PhotoPath;
        _ = Task.Run(() =>
        {
            var img = Thumbs.Load(path, 1100);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (CurrentFace?.PhotoPath == path) Photo = img;
            });
        });
    }

    private void RaiseAll()
    {
        Raise(nameof(CurrentFace)); Raise(nameof(HasWork)); Raise(nameof(IsEmpty));
        Raise(nameof(PositionText)); Raise(nameof(SuggestionText)); Raise(nameof(ConfidenceText));
        Raise(nameof(FileText)); Raise(nameof(FolderText));
    }
}
