using System.ComponentModel;
using System.Windows;
using FaceFlow.App.ViewModels;
using FaceFlow.Core;

namespace FaceFlow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Scanner.IsRunning)
        {
            var answer = MessageBox.Show(
                "A scan is still running.\n\n" +
                "Closing now is safe — everything already processed is saved, and the next " +
                "scan resumes from here instead of starting over.\n\nClose FaceFlow?",
                "Scan in progress", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _vm.Scanner.Cancel();
        }

        try { _vm.PersistSettings(); } catch (Exception ex) { Log.Warn("Could not save settings: " + ex.Message); }
        _vm.Dispose();
        base.OnClosing(e);
    }
}
