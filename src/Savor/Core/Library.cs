using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Savor.Core;

public sealed class ClipItem : INotifyPropertyChanged
{
    public required string FilePath { get; init; }
    public required DateTime Created { get; init; }
    public required long Size { get; init; }

    public ClipMeta? Info { get; init; }

    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public string Kind => IsRecording ? "REC" : "CLIP";
    public bool IsRecording => Info?.IsRecording ?? Name.StartsWith("Recording", StringComparison.OrdinalIgnoreCase);
    /// <summary>The game it was captured in, if Savor knows.</summary>
    public string? Game => Info?.Game;

    // inline rename on the library card
    bool _renaming;
    public bool IsRenaming { get => _renaming; set { _renaming = value; OnChanged(); } }
    public string Meta => $"{FormatSize(Size)}  ·  {Created.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)}";

    ImageSource? _thumb;
    public ImageSource? Thumbnail { get => _thumb; set { _thumb = value; OnChanged(); } }

    string _duration = "";
    public string Duration { get => _duration; set { _duration = value; OnChanged(); OnChanged(nameof(HasDuration)); } }
    public bool HasDuration => !string.IsNullOrEmpty(_duration);

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, b / 1024)} KB"
    };
}

public static class Library
{
    static readonly string ThumbDir = Path.Combine(AppSettings.LocalDataDir, "thumbs");
    static readonly SemaphoreSlim Gate = new(2);
    static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)", RegexOptions.Compiled);

    // capture info is tiny but read per file; cache it by path + write time
    static readonly Dictionary<string, (long ticks, ClipMeta? meta)> MetaCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<ClipItem> Scan(string folder, int max = 2000)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            var files = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .ToList();
            var list = new List<ClipItem>(files.Count);
            lock (MetaCache)
                foreach (var f in files)
                {
                    long ticks = f.LastWriteTimeUtc.Ticks;
                    if (!MetaCache.TryGetValue(f.FullName, out var c) || c.ticks != ticks)
                        MetaCache[f.FullName] = c = (ticks, ClipMeta.Read(f.FullName));
                    list.Add(new ClipItem { FilePath = f.FullName, Created = f.LastWriteTime, Size = f.Length, Info = c.meta });
                }
            return list;
        }
        catch { return new(); }
    }

    /// <summary>
    /// Renames a capture (keeping its extension). Returns the new path. Throws with a friendly message when the name
    /// can't be used. Savor's info stream and the cached thumbnail move with the file.
    /// </summary>
    public static string Rename(string path, string newName)
    {
        var name = ClipMeta.SafeFileName(newName ?? "");
        if (name.Length == 0) throw new InvalidOperationException("Type a name for the capture.");
        var dir = Path.GetDirectoryName(path)!;
        var target = Path.Combine(dir, name + Path.GetExtension(path));
        if (string.Equals(target, path, StringComparison.Ordinal)) return path;
        bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(target)) throw new InvalidOperationException($"There's already a capture called “{name}”.");

        string? oldKey = null;
        try { oldKey = ThumbKey(new FileInfo(path)); } catch { }
        var meta = ClipMeta.Read(path); // older captures only have it in their name: keep it once renamed

        // the file can be briefly locked right after the viewer lets go of it
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(path, target); break; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(150); }
            catch (IOException) { throw new InvalidOperationException("The file is in use by another app. Close it and try again."); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Windows didn't allow renaming this file."); }
        }

        meta?.Write(target);
        try
        {
            if (oldKey != null)
            {
                var newKey = ThumbKey(new FileInfo(target));
                foreach (var ext in new[] { ".jpg", ".txt" })
                {
                    var from = Path.Combine(ThumbDir, oldKey + ext);
                    if (File.Exists(from)) File.Move(from, Path.Combine(ThumbDir, newKey + ext), true);
                }
            }
        }
        catch { }
        lock (MetaCache) MetaCache.Remove(path);
        return target;
    }

    static string ThumbKey(FileInfo fi) => Hash($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");

    /// <summary>Loads or generates a cached thumbnail + duration for a clip (on a background thread).</summary>
    public static async Task LoadThumbnailAsync(ClipItem item)
    {
        string key;
        try
        {
            key = ThumbKey(new FileInfo(item.FilePath));
        }
        catch { return; }

        var jpg = Path.Combine(ThumbDir, key + ".jpg");
        var meta = Path.Combine(ThumbDir, key + ".txt");

        if (!File.Exists(jpg) && Ffmpeg.Path != null)
        {
            await Gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(ThumbDir);
                string err = "";
                foreach (var seek in new[] { "1", "0" })
                {
                    (_, err) = await Ffmpeg.RunAsync(new[]
                    {
                        "-hide_banner", "-nostdin", "-y", "-ss", seek, "-i", item.FilePath,
                        "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", jpg
                    }, TimeSpan.FromSeconds(20));
                    if (File.Exists(jpg)) break;
                }
                var m = DurationRx.Match(err);
                if (m.Success)
                {
                    var ts = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                    File.WriteAllText(meta, FormatDuration(ts));
                }
            }
            catch { }
            finally { Gate.Release(); }
        }

        try
        {
            if (File.Exists(meta)) item.Duration = File.ReadAllText(meta).Trim();
            if (File.Exists(jpg))
            {
                var bytes = await File.ReadAllBytesAsync(jpg);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = 360; // card size; keeps each thumbnail ~0.3 MB in memory
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                item.Thumbnail = bmp;
            }
        }
        catch { }
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
