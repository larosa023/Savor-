using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Savor.Core;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size);

/// <summary>
/// Checks GitHub Releases for a newer Savor.exe and swaps it in place:
/// Windows lets a running exe be renamed, so the old one becomes Savor.old.exe,
/// the new one takes its name, and the new one is launched.
/// </summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/larosa023/Savor-/releases/latest";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Savor", Current.ToString()));
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
            ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
            if (ver <= Current) return null;

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), "Savor.exe", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object) return null;
            return new UpdateInfo(ver, tag,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64());
        }
        catch { return null; }
    }

    /// <summary>Downloads the update and swaps the exe. Caller should exit right after this returns.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Savor.exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var name = Path.GetFileNameWithoutExtension(exe);
        var download = Path.Combine(dir, name + ".update");
        var old = Path.Combine(dir, name + ".old.exe");

        try
        {
            using (var resp = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? update.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(download);
                var buf = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report(read / (double)total);
                }
            }

            var fi = new FileInfo(download);
            if (fi.Length < 1_000_000 || (update.Size > 0 && fi.Length != update.Size))
                throw new InvalidOperationException("The download was incomplete. Try again.");
            using (var fs = File.OpenRead(download))
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                    throw new InvalidOperationException("The download isn't a valid program.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Savor can't write to its own folder. Move Savor.exe somewhere you own (like Desktop or Documents) and try again.");
        }
        catch
        {
            try { File.Delete(download); } catch { }
            throw;
        }

        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(download, exe);
        }
        catch (Exception ex)
        {
            // put things back if the swap half-failed
            try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
            try { File.Delete(download); } catch { }
            throw new InvalidOperationException("Couldn't replace Savor.exe: " + ex.Message);
        }

        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var old = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".old.exe");
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { System.Threading.Thread.Sleep(500); }
            }
        }
        catch { }
    }
}
