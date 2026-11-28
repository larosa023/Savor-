using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Savor.Core;

/// <summary>
/// What Savor knows about a capture: whether it is a clip or a recording, and which game it came from.
/// Stored in a tiny NTFS stream on the file itself ("file.mp4:savor"), so it stays with the file when it is
/// renamed or moved (in Savor or in Explorer) and nothing extra shows up in the folder. When the stream is
/// missing (older captures, FAT/exFAT drives) it falls back to reading Savor's file name pattern.
/// </summary>
public sealed record ClipMeta(string Kind, string? Game)
{
    public const string Clip = "clip", Recording = "recording", Screenshot = "screenshot";
    const string Stream = ":savor";

    // "<anything> 2026-10-30 21.14.05" (optionally " (2)") — the timestamp Savor puts in every name
    static readonly Regex Stamped = new(@"^(?<pre>.*?)\s*\d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2}( \(\d+\))?$", RegexOptions.Compiled);

    public bool IsRecording => Kind == Recording;

    /// <summary>Moments marked with the marker hotkey, in seconds from the start of the file.</summary>
    public double[] Markers { get; init; } = Array.Empty<double>();

    /// <summary>The game part of a Savor-made name ("VALORANT 2026-10-30 21.14.05" → "VALORANT"), or null.</summary>
    public static string? StampedPrefix(string name)
    {
        var m = Stamped.Match(name);
        return m.Success ? m.Groups["pre"].Value.Trim() : null;
    }

    public void Write(string path)
    {
        var lines = new List<string> { "kind=" + Kind };
        if (!string.IsNullOrWhiteSpace(Game)) lines.Add("game=" + Game.Replace('\n', ' ').Replace('\r', ' '));
        if (Markers.Length > 0)
            lines.Add("markers=" + string.Join(",", Markers.Select(m => m.ToString("0.###", CultureInfo.InvariantCulture))));
        bool stored = false;
        try
        {
            // writing the stream counts as modifying the file: keep its date so the library order doesn't change
            var when = File.GetLastWriteTimeUtc(path);
            File.WriteAllLines(path + Stream, lines);
            File.SetLastWriteTimeUtc(path, when);
            // some drives (exFAT/FAT, some synced folders) accept the write but don't keep it: check
            stored = File.ReadAllLines(path + Stream).SequenceEqual(lines);
        }
        catch { }
        if (stored) Index.Forget(path);
        else Index.Put(path, lines.ToArray());
    }

    /// <summary>Drops what Savor's own record holds for a file (after it was renamed or deleted).</summary>
    public static void Forget(string path) => Index.Forget(path);

    /// <summary>
    /// Savor's own record of capture info, for files whose drive can't keep the hidden stream (exFAT/FAT USB drives,
    /// some synced folders). Kept in Savor's data folder, keyed by the file's full path.
    /// </summary>
    static class Index
    {
        static readonly string File_ = System.IO.Path.Combine(AppSettings.LocalDataDir, "capture-info.json");
        static Dictionary<string, string[]>? _map;
        static readonly object Lock = new();

        static Dictionary<string, string[]> Map()
        {
            if (_map != null) return _map;
            try
            {
                _map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(File_));
            }
            catch { }
            _map = new Dictionary<string, string[]>(_map ?? new(), StringComparer.OrdinalIgnoreCase);
            return _map;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppSettings.LocalDataDir);
                // forget files that no longer exist, so the record never grows without end
                foreach (var k in _map!.Keys.Where(k => !File.Exists(k)).ToList()) _map.Remove(k);
                var tmp = File_ + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(_map));
                File.Move(tmp, File_, overwrite: true);
            }
            catch { }
        }

        public static string[]? Get(string path) { lock (Lock) return Map().TryGetValue(path, out var l) ? l : null; }
        public static void Put(string path, string[] lines) { lock (Lock) { Map()[path] = lines; Save(); } }
        public static void Forget(string path)
        {
            lock (Lock) { if (Map().Remove(path)) Save(); }
        }
    }

    /// <summary>Reads the stream, or works it out from the name. Null when the file doesn't look like a Savor capture.</summary>
    public static ClipMeta? Read(string path)
    {
        try
        {
            string[]? lines = null;
            try { lines = File.ReadAllLines(path + Stream); } catch { } // no stream (or not NTFS)
            if (lines == null || lines.Length == 0) lines = Index.Get(path); // Savor's own record
            if (lines != null)
            {
                string kind = Clip;
                string? game = null;
                var markers = new List<double>();
                foreach (var line in lines)
                {
                    if (line.StartsWith("kind=")) kind = line[5..].Trim();
                    else if (line.StartsWith("game=")) game = line[5..].Trim();
                    else if (line.StartsWith("markers="))
                        foreach (var part in line[8..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 0) markers.Add(m);
                }
                return new ClipMeta(kind is Recording or Screenshot ? kind : Clip, string.IsNullOrWhiteSpace(game) ? null : game)
                    { Markers = markers.OrderBy(m => m).ToArray() };
            }
        }
        catch { }
        return FromName(Path.GetFileNameWithoutExtension(path));
    }

    public static ClipMeta? FromName(string name)
    {
        if (name.StartsWith("Recording ", StringComparison.OrdinalIgnoreCase) && Stamped.IsMatch(name))
            return new ClipMeta(Recording, null);
        var m = Stamped.Match(name);
        if (!m.Success) return null;
        var pre = m.Groups["pre"].Value.Trim();
        if (pre.Length == 0 || pre.Equals("Clip", StringComparison.OrdinalIgnoreCase)) return new ClipMeta(Clip, null);
        if (pre.Equals("Screenshot", StringComparison.OrdinalIgnoreCase)) return new ClipMeta(Screenshot, null);
        if (pre.EndsWith(" Recording", StringComparison.OrdinalIgnoreCase))
            return new ClipMeta(Recording, pre[..^" Recording".Length].Trim());
        return new ClipMeta(Clip, pre);
    }

    /// <summary>A game name as stored and used in file names ("Jedi: Survivor" → "Jedi Survivor"); null if nothing is left.</summary>
    public static string? CleanGame(string? game)
    {
        if (game == null) return null;
        var g = SafeFileName(game);
        return g.Length == 0 ? null : g;
    }

    /// <summary>Makes a game name safe to use in a file name.</summary>
    public static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length > 60 ? s[..60].Trim() : s;
    }
}
