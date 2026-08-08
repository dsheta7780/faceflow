using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FaceFlow.Core;

namespace FaceFlow.App.ViewModels;

/// <summary>
/// Bounded LRU-ish cache of decoded thumbnails. Everything is decoded at a small
/// DecodePixelWidth and frozen so it can cross threads and be shared by the UI.
/// </summary>
public static class Thumbs
{
    private const int MaxEntries = 4000;
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();
    private static readonly ConcurrentQueue<string> Order = new();

    public static ImageSource? Placeholder { get; set; }

    private static void Remember(string key, ImageSource img)
    {
        if (Cache.TryAdd(key, img))
        {
            Order.Enqueue(key);
            while (Order.Count > MaxEntries && Order.TryDequeue(out var old))
                Cache.TryRemove(old, out _);
        }
    }

    public static ImageSource? Load(string path, int width)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = width + "|" + path;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = width;
            bmp.EndInit();
            bmp.Freeze();
            Remember(key, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail load failed for '{path}': {ex.Message}");
            return null;
        }
    }

    public static ImageSource? Face(long faceId, int width = 180)
        => Load(AppPaths.FaceThumbPath(faceId), width);

    public static void Clear()
    {
        Cache.Clear();
        while (Order.TryDequeue(out _)) { }
    }
}
