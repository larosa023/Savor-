using System;
using System.IO;
using System.Media;

namespace Savor.Core;

/// <summary>A short, quiet two-note chime played when a capture is saved (optional, off by default).</summary>
public static class Chime
{
    static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            var player = new SoundPlayer(new MemoryStream(_wav));
            player.Play(); // asynchronous, never blocks
        }
        catch { }
    }

    /// <summary>Generates the chime as a tiny 16-bit WAV (no sound files shipped).</summary>
    static byte[] Build()
    {
        const int rate = 44100;
        var notes = new (double freq, double secs)[] { (880, 0.07), (1318.5, 0.11) };
        int total = 0;
        foreach (var n in notes) total += (int)(n.secs * rate);
        var samples = new short[total];
        int pos = 0;
        foreach (var (freq, secs) in notes)
        {
            int len = (int)(secs * rate);
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Exp(-t * 18); // soft attack, quick fade
                samples[pos++] = (short)(Math.Sin(2 * Math.PI * freq * t) * env * 0.22 * short.MaxValue);
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
