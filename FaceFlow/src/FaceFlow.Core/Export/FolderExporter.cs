using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FaceFlow.Core.Export;

public enum ExportMode
{
    /// <summary>Byte-for-byte file copy. Always safe, uses disk space.</summary>
    Copy,
    /// <summary>NTFS hard link — same volume only, zero extra disk space, same bytes on disk.</summary>
    HardLink
}

public sealed class ExportResult
{
    public int Written;
    public int Skipped;
    public int Failed;
    public string Destination = "";
    public List<string> Errors = new();
}

/// <summary>
/// Creates organised folders WITHOUT ever touching the originals.
///
/// Hard guarantees, enforced here and nowhere else in the codebase:
///   - source files are only ever opened for READ
///   - no resize, no re-encode, no format conversion, no EXIF rewrite
///   - no move, no rename, no delete of anything under the source library
///   - the destination folder must not sit inside the source library
/// </summary>
public static class FolderExporter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static readonly Regex Invalid = new("[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]");

    public static string SafeFolderName(string name)
    {
        var clean = Invalid.Replace(name, "_").Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(clean)) clean = "Unnamed";
        return clean.Length > 120 ? clean[..120] : clean;
    }

    /// <summary>Long-path-safe form for the Win32 APIs (&gt; 260 chars).</summary>
    private static string Long(string p)
        => p.StartsWith(@"\\?\") ? p : (p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p);

    public static ExportResult Export(
        IEnumerable<string> sourcePaths,
        string destinationRoot,
        string folderName,
        ExportMode mode = ExportMode.Copy,
        IEnumerable<string>? protectedRoots = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ExportResult();
        var files = sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dest = Path.Combine(Path.GetFullPath(destinationRoot), SafeFolderName(folderName));

        // ---- refuse to write anywhere inside a watched photo library
        foreach (var root in protectedRoots ?? Array.Empty<string>())
        {
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (dest.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to export into your photo library. Choose a destination outside " +
                    root + " so your originals stay untouched.");
        }

        Directory.CreateDirectory(dest);
        result.Destination = dest;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(src)) { result.Skipped++; continue; }

                var name = Path.GetFileName(src);
                var target = Path.Combine(dest, name);

                // Collision handling: never overwrite, never rename the source.
                if (used.Contains(target) || File.Exists(target))
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    int n = 2;
                    do { target = Path.Combine(dest, $"{stem} ({n++}){ext}"); }
                    while (used.Contains(target) || File.Exists(target));
                }
                used.Add(target);

                bool ok = false;
                if (mode == ExportMode.HardLink)
                {
                    ok = CreateHardLinkW(Long(target), Long(src), IntPtr.Zero);
                    if (!ok) Log.Warn($"Hard link failed for '{src}' (different volume?), copying instead.");
                }
                if (!ok)
                    File.Copy(Long(src), Long(target), overwrite: false);   // READ source, WRITE new file only

                result.Written++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Errors.Count < 25) result.Errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
                Log.Warn($"Export failed for '{src}': {ex.Message}");
            }
            finally
            {
                progress?.Report((++done, files.Count));
            }
        }

        Log.Info($"Exported {result.Written} file(s) to '{dest}' ({mode}). " +
                 $"Skipped {result.Skipped}, failed {result.Failed}.");
        return result;
    }
}
