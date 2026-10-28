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

    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public string Kind => Name.StartsWith("Recording", StringComparison.OrdinalIgnoreCase) ? "REC" : "CLIP";
    public bool IsRecording => Kind == "REC";
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

    public static List<ClipItem> Scan(string folder, int max = 60)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            return new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .Select(f => new ClipItem { FilePath = f.FullName, Created = f.LastWriteTime, Size = f.Length })
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Loads or generates a cached thumbnail + duration for a clip (on a background thread).</summary>
    public static async Task LoadThumbnailAsync(ClipItem item)
    {
        string key;
        try
        {
            var fi = new FileInfo(item.FilePath);
            key = Hash($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
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
