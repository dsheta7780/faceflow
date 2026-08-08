using System.Diagnostics;
using System.Windows;
using FaceFlow.App.Mvvm;
using FaceFlow.Core;
using FaceFlow.Core.Faces;

namespace FaceFlow.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;

        Save = new RelayCommand(() =>
        {
            _main.PersistSettings();
            _main.Status = "Settings saved. They apply to the next scan.";
        });

        OpenDataFolder = new RelayCommand(() => Open(AppPaths.Root));
        OpenModelsFolder = new RelayCommand(() => Open(AppPaths.ModelsDir));
        OpenLogs = new RelayCommand(() => Open(AppPaths.LogsDir));

        ClearThumbnails = new RelayCommand(() =>
        {
            var answer = MessageBox.Show(
                "Delete all cached face thumbnails?\n\n" +
                "They are regenerated on the next scan. No photo files are affected.",
                "Clear thumbnail cache", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            try
            {
                Thumbs.Clear();
                foreach (var d in Directory.GetDirectories(AppPaths.ThumbsDir))
                    Directory.Delete(d, true);
                _main.Status = "Thumbnail cache cleared.";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "FaceFlow"); }
        });

        ResetIndex = new RelayCommand(() =>
        {
            var answer = MessageBox.Show(
                "Reset the FaceFlow index?\n\n" +
                "All people, faces and scan history are erased and the next scan starts fresh.\n\n" +
                "YOUR PHOTOS ARE NOT TOUCHED — this only clears FaceFlow's own database.",
                "Reset index", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            _main.Db.Write("DELETE FROM faces");
            _main.Db.Write("DELETE FROM people");
            _main.Db.Write("UPDATE photos SET state=0, face_count=-1, indexed_at=NULL, error=NULL");
            _main.Status = "Index reset. Run a scan to rebuild it.";
            _main.Dashboard.Refresh();
            _main.People.Refresh();
        });
    }

    public int Workers
    {
        get => _main.ScanSettings.Workers;
        set { _main.ScanSettings.Workers = Math.Clamp(value, 1, 64); Raise(); }
    }

    public int DecodeMaxDimension
    {
        get => _main.ScanSettings.DecodeMaxDimension;
        set { _main.ScanSettings.DecodeMaxDimension = Math.Clamp(value, 640, 4096); Raise(); }
    }

    public bool PreferGpu
    {
        get => _main.ScanSettings.PreferGpu;
        set { _main.ScanSettings.PreferGpu = value; Raise(); }
    }

    public bool GenerateThumbnails
    {
        get => _main.ScanSettings.GenerateThumbnails;
        set { _main.ScanSettings.GenerateThumbnails = value; Raise(); }
    }

    public string HardwareText => HardwareInfo.Describe();
    public string ProvidersText => string.Join(", ", HardwareInfo.AvailableProviders());
    public string DataFolder => AppPaths.Root;
    public string ModelsFolder => AppPaths.ModelsDir;
    public int CoreCount => Environment.ProcessorCount;

    public RelayCommand Save { get; }
    public RelayCommand OpenDataFolder { get; }
    public RelayCommand OpenModelsFolder { get; }
    public RelayCommand OpenLogs { get; }
    public RelayCommand ClearThumbnails { get; }
    public RelayCommand ResetIndex { get; }

    private static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Open folder failed: " + ex.Message); }
    }
}
