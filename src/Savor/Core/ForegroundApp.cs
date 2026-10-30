using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Savor.Core;

/// <summary>
/// Works out a friendly name ("VALORANT", "Minecraft", "Rocket League") for the app in the foreground,
/// so clips can be named and grouped by game. Returns null for the desktop, Windows' own UI and Savor.
/// </summary>
public static class ForegroundApp
{
    static readonly string[] Ignored =
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "lockapp",
        "textinputhost", "applicationframehost", "dwm", "taskmgr", "systemsettings", "savor", "logonui",
        "consent", "rundll32", "snippingtool", "screenclippinghost", "conhost", "openconsole",
    };

    // names that say nothing about the game (engines, runtimes, launch stubs)
    static readonly Regex Generic = new(
        @"^(java|javaw|openjdk.*|java\(tm\).*|.*platform se binary.*|unreal.*|ue[45].*|bootstrap.*|game|application|" +
        @"launcher|unity.*|win64|shipping|client|.*shipping.*|main|app|player|host|runtime.*|electron)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex TrailingVersion = new(@"\s*[-–—]?\s*v?\d+(\.\d+)+[a-z0-9.\-]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? CurrentName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            var exe = ImagePath(pid);
            if (exe == null) return null;
            var stem = Path.GetFileNameWithoutExtension(exe);
            if (Ignored.Contains(stem.ToLowerInvariant())) return null;

            string? desc = null, product = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                desc = vi.FileDescription;
                product = vi.ProductName;
            }
            catch { }

            foreach (var candidate in new[] { Clean(desc), Clean(product), FromTitle(hwnd), FromExe(stem) })
                if (Usable(candidate)) return candidate;
            return null;
        }
        catch { return null; }
    }

    static bool Usable(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length is >= 2 and <= 40 && !Generic.IsMatch(s);

    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        s = Regex.Replace(s, @"\s+", " ").Trim().Trim('*', '-', ' ');
        s = TrailingVersion.Replace(s, "").Trim();
        return s.Length == 0 ? null : s;
    }

    static string? FromTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetWindowText(hwnd, sb, sb.Capacity) <= 0) return null;
        var t = sb.ToString();
        // "Minecraft* 1.21.4 - Singleplayer" → "Minecraft"
        foreach (var sep in new[] { " - ", " — ", " – ", " | " })
        {
            int i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) t = t[..i];
        }
        return Clean(t);
    }

    static string? FromExe(string stem)
    {
        var s = Regex.Replace(stem, @"([-_]?(Win64|Win32|x64|x86|Shipping|DX11|DX12|Vulkan))+$", "", RegexOptions.IgnoreCase);
        s = s.Replace('_', ' ').Replace('-', ' ');
        return Clean(s);
    }

    static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}
