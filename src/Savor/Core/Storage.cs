using System;
using System.IO;
using System.Linq;

namespace Savor.Core;

/// <summary>Keeps the captures folder under a size limit and checks free disk space.</summary>
public static class Storage
{
    public const long LowSpaceWarnBytes = 2L << 30;      // 2 GB: warn
    public const long CriticalSpaceBytes = 500L << 20;   // 500 MB: stop recording safely

    /// <summary>Only files Savor made (clips, recordings and their edits) are ever touched.</summary>
    static bool IsSavorCapture(FileInfo f) =>
        (f.Name.StartsWith("Clip ", StringComparison.OrdinalIgnoreCase) ||
         f.Name.StartsWith("Recording ", StringComparison.OrdinalIgnoreCase)) &&
        (f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
         f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes the oldest captures until the folder's Savor captures total at most <paramref name="limitGb"/>.
    /// Never deletes <paramref name="keep"/> (the capture that was just saved).
    /// </summary>
    public static (int Deleted, long Freed) Enforce(string folder, int limitGb, string? keep = null) =>
        limitGb <= 0 ? (0, 0) : EnforceBytes(folder, (long)limitGb << 30, keep);

    public static (int Deleted, long Freed) EnforceBytes(string folder, long limit, string? keep = null)
    {
        if (limit <= 0 || !Directory.Exists(folder)) return (0, 0);
        var files = new DirectoryInfo(folder).EnumerateFiles().Where(IsSavorCapture).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        int deleted = 0;
        long freed = 0;
        foreach (var f in files)
        {
            if (total <= limit) break;
            if (keep != null && string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                long len = f.Length;
                f.Delete();
                total -= len;
                freed += len;
                deleted++;
            }
            catch { /* in use (e.g. open in a player): skip it */ }
        }
        return (deleted, freed);
    }

    public static long UsedBytes(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Where(IsSavorCapture).Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>Free space on the drive holding <paramref name="path"/>, or long.MaxValue if unknown.</summary>
    public static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return long.MaxValue; }
    }

    public static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
