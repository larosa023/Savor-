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

    bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; OnChanged(); } }

    ImageSource? _hover;
    /// <summary>Frame shown while the mouse is over the card (null = the thumbnail).</summary>
    public ImageSource? HoverFrame { get => _hover; set { _hover = value; OnChanged(); } }

    // inline rename on the library card
    bool _renaming;
    /// <summary>The inline box edits the game instead of the file name.</summary>
    public bool EditingGame { get; set; }
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

    /// <summary>
    /// Renames a game/app category: retags every capture in it, and renames files that still have Savor's
    /// automatic name ("Claude 2026-10-31 00.00.12" → "NBA 2026-10-31 00.00.12"). Names the user chose stay as they are.
    /// Returns how many captures were moved to the new name.
    /// </summary>
    public static int RenameGame(string folder, string oldGame, string newGame)
    {
        newGame = ClipMeta.SafeFileName(newGame ?? "");
        if (newGame.Length == 0) throw new InvalidOperationException("Type a name for the category.");
        int n = 0;
        foreach (var c in Scan(folder))
        {
            if (!string.Equals(c.Game, oldGame, StringComparison.OrdinalIgnoreCase)) continue;
            var path = c.FilePath;
            try { Retag(path, c.IsRecording, oldGame, newGame); n++; }
            catch { }
        }
        lock (MetaCache) MetaCache.Clear();
        return n;
    }

    /// <summary>
    /// Moves one capture to another game (or to none). A Savor-made name follows along
    /// ("Claude 2026-10-31 00.00.12" → "NBA 2026-10-31 00.00.12"); a name the user typed is kept. Returns the new path.
    /// </summary>
    public static string SetGame(ClipItem c, string? game)
    {
        if (game != null)
        {
            game = ClipMeta.SafeFileName(game);
            if (game.Length == 0) game = null;
        }
        var path = Retag(c.FilePath, c.IsRecording, c.Game, game);
        lock (MetaCache) { MetaCache.Remove(c.FilePath); MetaCache.Remove(path); }
        return path;
    }

    static string Retag(string path, bool recording, string? oldGame, string? newGame)
    {
        new ClipMeta(recording ? ClipMeta.Recording : ClipMeta.Clip, newGame).Write(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var pre = ClipMeta.StampedPrefix(name);
        if (pre == null) return path; // the user's own name: leave it
        string oldPrefix = recording
            ? (oldGame != null ? oldGame + " Recording" : "Recording")
            : (oldGame ?? "Clip");
        if (!string.Equals(pre, oldPrefix, StringComparison.OrdinalIgnoreCase)) return path;
        string newPrefix = recording
            ? (newGame != null ? newGame + " Recording" : "Recording")
            : (newGame ?? "Clip");
        try { return Rename(path, newPrefix + name[pre.Length..]); }
        catch { return path; } // name taken or file busy: the tag is enough
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

    const int PreviewFrames = 8;

    /// <summary>
    /// A few small frames spread across the clip, for the hover preview. Made once with ffmpeg (keyframes only for
    /// longer clips, so it's quick) and cached as one strip image; only kept in memory while hovered.
    /// </summary>
    public static async Task<BitmapSource[]?> LoadPreviewFramesAsync(ClipItem item, CancellationToken ct)
    {
        string key;
        try { key = ThumbKey(new FileInfo(item.FilePath)); } catch { return null; }
        var sprite = Path.Combine(ThumbDir, key + "_p.jpg");
        if (!File.Exists(sprite))
        {
            if (Ffmpeg.Path == null) return null;
            double dur = ParseDuration(item.Duration) ?? 30;
            await Gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(sprite))
                {
                    Directory.CreateDirectory(ThumbDir);
                    // a little over N frames per clip so the strip always fills up
                    var rate = ((PreviewFrames + 1) / Math.Max(1, dur)).ToString("0.#####", CultureInfo.InvariantCulture);
                    var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
                    if (dur > 20) args.AddRange(new[] { "-skip_frame", "nokey" });
                    args.AddRange(new[]
                    {
                        "-i", item.FilePath, "-an",
                        "-vf", $"fps={rate},scale=320:-2:flags=bilinear,tile={PreviewFrames}x1",
                        "-frames:v", "1", "-q:v", "5", sprite
                    });
                    await Ffmpeg.RunAsync(args, TimeSpan.FromSeconds(30), ct);
                }
            }
            finally { Gate.Release(); }
        }
        if (!File.Exists(sprite) || ct.IsCancellationRequested) return null;

        var bytes = await File.ReadAllBytesAsync(sprite, ct);
        return await Task.Run(() =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            int w = bmp.PixelWidth / PreviewFrames, h = bmp.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            var frames = new List<BitmapSource>();
            for (int i = 0; i < PreviewFrames; i++)
            {
                var f = new CroppedBitmap(bmp, new System.Windows.Int32Rect(i * w, 0, w, h));
                f.Freeze();
                frames.Add(f);
            }
            return frames.ToArray();
        }, ct);
    }

    static double? ParseDuration(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        double total = 0;
        foreach (var part in s.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
