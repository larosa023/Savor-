using System;
using System.IO;

namespace Savor.Core;

/// <summary>
/// The RNNoise model used for mic noise suppression (ffmpeg's arnndn filter). It ships inside Savor.exe
/// and is unpacked once next to the other app data. ("leavened-quisling" from GregorR/rnnoise-models:
/// trained for voice over general background noise like keyboards, fans and hum.)
/// </summary>
public static class NoiseModel
{
    public const string FileName = "noise.rnnn";
    static string? _dir;

    /// <summary>Folder holding the model (ffmpeg runs there so the filter can use a plain relative name), or null.</summary>
    public static string? Directory
    {
        get
        {
            if (_dir != null) return _dir;
            try
            {
                var dir = Path.Combine(AppSettings.LocalDataDir, "noise");
                var file = Path.Combine(dir, FileName);
                using var res = typeof(NoiseModel).Assembly.GetManifestResourceStream(FileName);
                if (res == null) return null;
                if (!File.Exists(file) || new FileInfo(file).Length != res.Length)
                {
                    System.IO.Directory.CreateDirectory(dir);
                    var tmp = file + ".partial";
                    using (var fs = File.Create(tmp)) res.CopyTo(fs);
                    File.Move(tmp, file, true);
                }
                return _dir = dir;
            }
            catch { return null; }
        }
    }
}
