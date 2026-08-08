using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FaceFlow.Core.Imaging;

/// <summary>
/// A plain RGB byte buffer. Decoding goes through WIC (BitmapDecoder), which is the
/// fastest decoder available on Windows and supports JPEG/PNG/TIFF/BMP/GIF natively,
/// plus HEIC and camera RAW when the matching Windows codec is installed.
/// </summary>
public sealed class RgbImage
{
    public readonly byte[] Pixels;   // RGB, row-major, 3 bytes per pixel
    public readonly int Width;
    public readonly int Height;
    public readonly int OriginalWidth;
    public readonly int OriginalHeight;
    public float ScaleToOriginal => OriginalWidth <= 0 ? 1f : (float)OriginalWidth / Width;

    private RgbImage(byte[] px, int w, int h, int ow, int oh)
        => (Pixels, Width, Height, OriginalWidth, OriginalHeight) = (px, w, h, ow, oh);

    public static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".jfif", ".dng", ".cr2", ".nef", ".arw", ".orf", ".rw2"
    };

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in SupportedExtensions)
            if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Decode at (at most) maxDimension on the long edge. WIC does the downscale during
    /// decode via DecodePixelWidth, so a 48 MP photo never fully materialises in memory.
    /// </summary>
    public static RgbImage Load(string path, int maxDimension)
    {
        int ow, oh;

        // Probe the real dimensions on their own stream. BitmapDecoder keeps a lazy hold on
        // whatever stream it is given, so the decode below gets a fresh one.
        using (var probeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                                bufferSize: 1 << 14, FileOptions.SequentialScan))
        {
            var probe = BitmapDecoder.Create(probeStream, BitmapCreateOptions.DelayCreation
                                                        | BitmapCreateOptions.IgnoreColorProfile,
                                             BitmapCacheOption.None);
            if (probe.Frames.Count == 0) throw new InvalidDataException("Image has no frames.");
            ow = probe.Frames[0].PixelWidth;
            oh = probe.Frames[0].PixelHeight;
        }
        if (ow <= 0 || oh <= 0) throw new InvalidDataException("Image has zero size.");

        int targetW, targetH;
        if (Math.Max(ow, oh) <= maxDimension) { targetW = ow; targetH = oh; }
        else if (ow >= oh) { targetW = maxDimension; targetH = Math.Max(1, (int)Math.Round(oh * (double)maxDimension / ow)); }
        else { targetH = maxDimension; targetW = Math.Max(1, (int)Math.Round(ow * (double)maxDimension / oh)); }

        var bmp = new BitmapImage();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                       bufferSize: 1 << 16, FileOptions.SequentialScan))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;      // fully decodes before EndInit returns
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = fs;
            bmp.DecodePixelWidth = targetW;
            bmp.EndInit();
        }
        bmp.Freeze();

        BitmapSource src = bmp;
        if (src.Format != PixelFormats.Bgr24)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            conv.Freeze();
            src = conv;
        }

        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 3;
        var bgr = new byte[stride * h];
        src.CopyPixels(bgr, stride, 0);

        // BGR -> RGB in place (both models expect RGB).
        for (int i = 0; i < bgr.Length; i += 3)
            (bgr[i], bgr[i + 2]) = (bgr[i + 2], bgr[i]);

        return new RgbImage(bgr, w, h, ow, oh);
    }

    public (byte R, byte G, byte B) At(int x, int y)
    {
        if (x < 0) x = 0; else if (x >= Width) x = Width - 1;
        if (y < 0) y = 0; else if (y >= Height) y = Height - 1;
        int i = (y * Width + x) * 3;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>Bilinear sample in source coordinates.</summary>
    public void Sample(float x, float y, out float r, out float g, out float b)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        var p00 = At(x0, y0); var p10 = At(x0 + 1, y0);
        var p01 = At(x0, y0 + 1); var p11 = At(x0 + 1, y0 + 1);
        r = (p00.R * (1 - fx) + p10.R * fx) * (1 - fy) + (p01.R * (1 - fx) + p11.R * fx) * fy;
        g = (p00.G * (1 - fx) + p10.G * fx) * (1 - fy) + (p01.G * (1 - fx) + p11.G * fx) * fy;
        b = (p00.B * (1 - fx) + p10.B * fx) * (1 - fy) + (p01.B * (1 - fx) + p11.B * fx) * fy;
    }

    /// <summary>Write a JPEG crop of the given rectangle (in this image's coordinates).</summary>
    public void SaveCropJpeg(string destPath, float x, float y, float w, float h, int size, int quality = 82)
    {
        // Expand to a square with 25% margin so the thumbnail isn't a tight, ugly crop.
        float cx = x + w / 2f, cy = y + h / 2f;
        float side = MathF.Max(w, h) * 1.5f;
        var px = new byte[size * size * 3];

        for (int j = 0; j < size; j++)
        {
            float sy = cy - side / 2f + (j + 0.5f) * side / size;
            for (int i = 0; i < size; i++)
            {
                float sx = cx - side / 2f + (i + 0.5f) * side / size;
                Sample(sx, sy, out var r, out var g, out var b);
                int o = (j * size + i) * 3;
                px[o] = (byte)Math.Clamp(b, 0, 255);      // BGR order for Bgr24
                px[o + 1] = (byte)Math.Clamp(g, 0, 255);
                px[o + 2] = (byte)Math.Clamp(r, 0, 255);
            }
        }

        var bs = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, px, size * 3);
        bs.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        enc.Save(outFs);
    }
}
