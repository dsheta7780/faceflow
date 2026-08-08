using System.Windows;
using System.Windows.Threading;
using FaceFlow.Core;

namespace FaceFlow.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"FaceFlow starting. Data: {AppPaths.Root}");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        var answer = MessageBox.Show(
            "FaceFlow hit an unexpected problem:\n\n" +
            e.Exception.Message +
            "\n\nYour index and your photos are safe. Continue running?\n\n" +
            $"Details were written to:\n{AppPaths.LogsDir}",
            "FaceFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        // Keep the app alive unless the user explicitly wants out — a scan in
        // progress has already committed everything it finished.
        e.Handled = answer == MessageBoxResult.Yes;
        if (!e.Handled) Shutdown(1);
    }
}
