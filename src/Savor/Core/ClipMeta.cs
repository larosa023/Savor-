using System;
using System.Collections.Generic;
using System.IO;
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
    public const string Clip = "clip", Recording = "recording";
    const string Stream = ":savor";

    // "<anything> 2026-10-30 21.14.05" (optionally " (2)") — the timestamp Savor puts in every name
    static readonly Regex Stamped = new(@"^(?<pre>.*?)\s*\d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2}( \(\d+\))?$", RegexOptions.Compiled);

    public bool IsRecording => Kind == Recording;

    /// <summary>The game part of a Savor-made name ("VALORANT 2026-10-30 21.14.05" → "VALORANT"), or null.</summary>
    public static string? StampedPrefix(string name)
    {
        var m = Stamped.Match(name);
        return m.Success ? m.Groups["pre"].Value.Trim() : null;
    }

    public void Write(string path)
    {
        try
        {
            var lines = new List<string> { "kind=" + Kind };
            if (!string.IsNullOrWhiteSpace(Game)) lines.Add("game=" + Game.Replace('\n', ' ').Replace('\r', ' '));
            // writing the stream counts as modifying the file: keep its date so the library order doesn't change
            var when = File.GetLastWriteTimeUtc(path);
            File.WriteAllLines(path + Stream, lines);
            File.SetLastWriteTimeUtc(path, when);
        }
        catch { } // non-NTFS drive: the name still carries the game
    }

    /// <summary>Reads the stream, or works it out from the name. Null when the file doesn't look like a Savor capture.</summary>
    public static ClipMeta? Read(string path)
    {
        try
        {
            string[]? lines = null;
            try { lines = File.ReadAllLines(path + Stream); } catch { } // no stream (or not NTFS)
            if (lines != null)
            {
                string kind = Clip;
                string? game = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("kind=")) kind = line[5..].Trim();
                    else if (line.StartsWith("game=")) game = line[5..].Trim();
                }
                return new ClipMeta(kind == Recording ? Recording : Clip, string.IsNullOrWhiteSpace(game) ? null : game);
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
        if (pre.EndsWith(" Recording", StringComparison.OrdinalIgnoreCase))
            return new ClipMeta(Recording, pre[..^" Recording".Length].Trim());
        return new ClipMeta(Clip, pre);
    }

    /// <summary>Makes a game name safe to use in a file name.</summary>
    public static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length > 60 ? s[..60].Trim() : s;
    }
}
