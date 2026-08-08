namespace FaceFlow.Core;

/// <summary>All on-disk locations FaceFlow uses. Nothing here ever touches your photo library.</summary>
public static class AppPaths
{
    public static string Root { get; }
    public static string DbPath => Path.Combine(Root, "faceflow.db");
    public static string ThumbsDir => Path.Combine(Root, "thumbs");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ModelsDir { get; }

    static AppPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceFlow");

        // Prefer a "models" folder shipped next to the executable; fall back to AppData.
        var beside = Path.Combine(AppContext.BaseDirectory, "models");
        ModelsDir = Directory.Exists(beside) ? beside : Path.Combine(Root, "models");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ModelsDir);
    }

    /// <summary>Face thumbnails are sharded 256 ways so no single folder holds millions of files.</summary>
    public static string FaceThumbPath(long faceId)
    {
        var shard = (faceId & 0xFF).ToString("x2");
        var dir = Path.Combine(ThumbsDir, shard);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, faceId + ".jpg");
    }
}
