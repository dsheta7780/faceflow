namespace FaceFlow.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string File_ = Path.Combine(AppPaths.LogsDir, $"faceflow-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
                System.IO.File.AppendAllText(File_, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}
