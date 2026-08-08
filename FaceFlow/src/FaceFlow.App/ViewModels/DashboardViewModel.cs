using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using FaceFlow.App.Mvvm;
using FaceFlow.Core.Data;
using FaceFlow.Core.Scanning;
using Microsoft.Win32;

namespace FaceFlow.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private LibraryStats _stats = new();
    private string _phase = "Idle";
    private string _detail = "";
    private double _percent;
    private double _rate;
    private bool _gpu;

    public ObservableCollection<LibraryRow> Libraries { get; } = new();

    public LibraryStats Stats { get => _stats; set { Set(ref _stats, value); Raise(nameof(HasWork)); } }
    public string Phase { get => _phase; set => Set(ref _phase, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public double Rate { get => _rate; set => Set(ref _rate, value); }
    public bool Gpu { get => _gpu; set { Set(ref _gpu, value); Raise(nameof(AcceleratorText)); } }
    public string AcceleratorText => Gpu ? "GPU" : "CPU";
    public bool HasWork => Stats.Pending > 0;
    public bool IsScanning => _main.Scanner.IsRunning;
    public bool IsPaused => _main.Scanner.IsPaused;

    public RelayCommand AddLibrary { get; }
    public RelayCommand RemoveLibrary { get; }
    public AsyncRelayCommand StartScan { get; }
    public RelayCommand PauseScan { get; }
    public RelayCommand CancelScan { get; }
    public RelayCommand OpenReview { get; }

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;

        AddLibrary = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Choose a photo folder to index",
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;
            _main.Repo.AddLibrary(dlg.FolderName);
            Refresh();
            _main.Status = $"Added {dlg.FolderName}. Press Start scan when you're ready.";
        });

        RemoveLibrary = new RelayCommand(p =>
        {
            if (p is not LibraryRow lib) return;
            var answer = MessageBox.Show(
                $"Remove '{lib.Path}' from FaceFlow?\n\n" +
                "This only forgets the index entries for those photos.\n" +
                "Nothing on disk is moved, renamed or deleted.",
                "Remove library", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            _main.Repo.RemoveLibrary(lib.Id);
            Refresh();
        }, p => p is LibraryRow);

        StartScan = new AsyncRelayCommand(async () =>
        {
            var libs = _main.Repo.GetLibraries();
            if (libs.Count == 0)
            {
                MessageBox.Show("Add a photo folder first.", "FaceFlow",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var missing = libs.Where(l => !Directory.Exists(l.Path)).ToList();
            if (missing.Count == libs.Count)
            {
                MessageBox.Show("None of your library folders are reachable right now.\n" +
                                "If they live on an external or network drive, connect it and try again.",
                                "FaceFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Raise(nameof(IsScanning));
            await _main.Scanner.ScanAsync(libs);
            Refresh();
            _main.People.Refresh();
            _main.RefreshCounters();
            Raise(nameof(IsScanning));
        }, () => !_main.Scanner.IsRunning);

        PauseScan = new RelayCommand(() =>
        {
            if (_main.Scanner.IsPaused) _main.Scanner.Resume(); else _main.Scanner.Pause();
            Raise(nameof(IsPaused));
        }, () => _main.Scanner.IsRunning);

        CancelScan = new RelayCommand(() => _main.Scanner.Cancel(), () => _main.Scanner.IsRunning);

        OpenReview = new RelayCommand(() => _main.Navigate(_main.Review));
    }

    public void Refresh()
    {
        Libraries.Clear();
        foreach (var l in _main.Repo.GetLibraries()) Libraries.Add(l);
        Stats = _main.Repo.GetStats();
    }

    public void ApplyProgress(ScanProgress p)
    {
        Phase = p.Phase switch
        {
            ScanPhase.Enumerating => "Building file index",
            ScanPhase.Processing => "Recognising faces",
            ScanPhase.Finishing => "Finalising",
            ScanPhase.Completed => "Up to date",
            ScanPhase.Cancelled => "Stopped — progress saved",
            ScanPhase.Failed => "Failed",
            _ => "Idle"
        };
        Detail = p.Phase == ScanPhase.Enumerating
            ? $"{p.FilesSeen:N0} files seen · {p.FilesQueued:N0} new or changed · {p.FilesSkipped:N0} unchanged"
            : $"{p.Processed:N0} of {p.Total:N0} · {p.FacesFound:N0} faces · {p.PeopleKnown:N0} people · {p.Failures:N0} skipped";
        Percent = p.Percent;
        Rate = p.PhotosPerSecond;
        Gpu = p.Gpu;

        if (p.Phase is ScanPhase.Completed or ScanPhase.Cancelled or ScanPhase.Failed)
            Stats = _main.Repo.GetStats();
        else if (p.Processed % 64 == 0)
            Stats = _main.Repo.GetStats();
    }
}
