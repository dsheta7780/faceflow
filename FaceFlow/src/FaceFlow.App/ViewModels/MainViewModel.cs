using System.Windows;
using System.Windows.Threading;
using FaceFlow.App.Mvvm;
using FaceFlow.Core;
using FaceFlow.Core.Data;
using FaceFlow.Core.Faces;
using FaceFlow.Core.Scanning;

namespace FaceFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private object? _current;
    private string _status = "Ready";
    private string _hardware = "";
    private double _progressPercent;
    private string _progressText = "";
    private bool _scanning;
    private long _reviewCount;

    public Db Db { get; }
    public Repository Repo { get; }
    public ScanEngine Scanner { get; }
    public ScanSettings ScanSettings { get; } = new();

    public DashboardViewModel Dashboard { get; }
    public PeopleViewModel People { get; }
    public ReviewViewModel Review { get; }
    public PhotosViewModel Photos { get; }
    public PhotosViewModel NoFaces { get; }
    public SettingsViewModel Settings { get; }

    public object? Current { get => _current; set => Set(ref _current, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Hardware { get => _hardware; set => Set(ref _hardware, value); }
    public double ProgressPercent { get => _progressPercent; set => Set(ref _progressPercent, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public bool IsScanning { get => _scanning; set { if (Set(ref _scanning, value)) Raise(nameof(ShowProgress)); } }
    public bool ShowProgress => IsScanning;
    public long ReviewCount { get => _reviewCount; set => Set(ref _reviewCount, value); }

    public RelayCommand GoDashboard { get; }
    public RelayCommand GoPeople { get; }
    public RelayCommand GoReview { get; }
    public RelayCommand GoPhotos { get; }
    public RelayCommand GoNoFaces { get; }
    public RelayCommand GoSettings { get; }

    private readonly DispatcherTimer _uiTimer;

    public MainViewModel()
    {
        Db = new Db();
        Repo = new Repository(Db);
        LoadPersistedSettings();
        Scanner = new ScanEngine(Repo, ScanSettings);

        Dashboard = new DashboardViewModel(this);
        People    = new PeopleViewModel(this);
        Review    = new ReviewViewModel(this);
        Photos    = new PhotosViewModel(this, PhotoFilter.All);
        NoFaces   = new PhotosViewModel(this, PhotoFilter.NoFaces);
        Settings  = new SettingsViewModel(this);

        GoDashboard = new RelayCommand(() => Navigate(Dashboard));
        GoPeople    = new RelayCommand(() => Navigate(People));
        GoReview    = new RelayCommand(() => Navigate(Review));
        GoPhotos    = new RelayCommand(() => Navigate(Photos));
        GoNoFaces   = new RelayCommand(() => Navigate(NoFaces));
        GoSettings  = new RelayCommand(() => Navigate(Settings));

        Scanner.ProgressChanged += OnScanProgress;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _uiTimer.Tick += (_, _) => RefreshCounters();
        _uiTimer.Start();

        Hardware = HardwareInfo.Describe();
        Current = Dashboard;
        Dashboard.Refresh();
        RefreshCounters();
    }

    public void Navigate(object page)
    {
        Current = page;
        switch (page)
        {
            case DashboardViewModel d: d.Refresh(); break;
            case PeopleViewModel p: p.Refresh(); break;
            case ReviewViewModel r: r.Refresh(); break;
            case PhotosViewModel ph: ph.Refresh(); break;
        }
    }

    public void OpenPerson(long personId)
    {
        People.OpenPerson(personId);
        Current = People;
    }

    private void OnScanProgress(ScanProgress p)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsScanning = p.Phase is ScanPhase.Enumerating or ScanPhase.Processing or ScanPhase.Finishing;
            ProgressPercent = p.Percent;

            ProgressText = p.Phase switch
            {
                ScanPhase.Enumerating => $"Indexing files — {p.FilesSeen:N0} seen, {p.FilesQueued:N0} new, {p.FilesSkipped:N0} unchanged",
                ScanPhase.Processing  => $"{p.Processed:N0} / {p.Total:N0} · {p.PhotosPerSecond:0.0}/s · {p.FacesFound:N0} faces" +
                                         (p.Eta is { } e ? $" · {Humanise(e)} left" : ""),
                ScanPhase.Finishing   => "Finalising index...",
                _ => p.Message ?? ""
            };

            Status = p.Message ?? Status;
            Dashboard.ApplyProgress(p);
        });
    }

    private static string Humanise(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
         : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
         : $"{(int)t.TotalSeconds}s";

    public void RefreshCounters()
    {
        try { ReviewCount = Repo.GetStats().NeedsReview; }
        catch { /* database busy; try again next tick */ }
    }

    private void LoadPersistedSettings()
    {
        int I(string k, int d) => int.TryParse(Db.GetSetting(k), out var v) ? v : d;
        bool B(string k, bool d) => bool.TryParse(Db.GetSetting(k), out var v) ? v : d;

        ScanSettings.Workers = Math.Clamp(I("workers", ScanSettings.Workers), 1, 64);
        ScanSettings.DecodeMaxDimension = Math.Clamp(I("decodeMax", 1600), 640, 4096);
        ScanSettings.PreferGpu = B("preferGpu", true);
        ScanSettings.GenerateThumbnails = B("thumbs", true);
    }

    public void PersistSettings()
    {
        Db.SetSetting("workers", ScanSettings.Workers.ToString());
        Db.SetSetting("decodeMax", ScanSettings.DecodeMaxDimension.ToString());
        Db.SetSetting("preferGpu", ScanSettings.PreferGpu.ToString());
        Db.SetSetting("thumbs", ScanSettings.GenerateThumbnails.ToString());
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        Scanner.Cancel();
        Scanner.Dispose();
        Db.Dispose();
    }
}
