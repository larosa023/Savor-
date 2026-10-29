using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Savor.Core;

/// <summary>
/// Graphics adapters and driver versions, read from the registry (no WMI, so nothing extra is loaded).
/// Used to explain why GPU encoding isn't available and to re-detect encoders after a driver update.
/// </summary>
public static class GpuInfo
{
    public sealed record Adapter(string Name, string Vendor, string DriverVersion)
    {
        /// <summary>The version people know: NVIDIA "32.0.15.6094" → "560.94"; others as installed.</summary>
        public string FriendlyVersion
        {
            get
            {
                if (Vendor != "nvidia") return DriverVersion;
                var p = DriverVersion.Split('.');
                if (p.Length != 4) return DriverVersion;
                var digits = p[2] + p[3].PadLeft(4, '0');
                if (digits.Length < 5) return DriverVersion;
                digits = digits[^5..];
                return $"{int.Parse(digits[..3])}.{digits[3..]}";
            }
        }
    }

    const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    static List<Adapter>? _cache;

    public static IReadOnlyList<Adapter> Adapters()
    {
        if (_cache != null) return _cache;
        var list = new List<Adapter>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ClassKey);
            if (root != null)
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        var name = k.GetValue("DriverDesc") as string;
                        var ver = k.GetValue("DriverVersion") as string ?? "";
                        var id = (k.GetValue("MatchingDeviceId") as string ?? "").ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string vendor = id.Contains("ven_10de") ? "nvidia"
                                      : id.Contains("ven_1002") || id.Contains("ven_1022") ? "amd"
                                      : id.Contains("ven_8086") ? "intel" : "";
                        if (vendor == "") continue; // virtual / remote display adapters
                        if (!list.Any(a => a.Name == name && a.DriverVersion == ver))
                            list.Add(new Adapter(name.Trim(), vendor, ver));
                    }
                    catch { }
                }
        }
        catch { }
        return _cache = list;
    }

    /// <summary>Changes whenever a graphics card or driver changes, so cached encoder results are re-checked.</summary>
    public static string Stamp() => string.Join(";", Adapters().Select(a => $"{a.Vendor}:{a.Name}:{a.DriverVersion}"));
}
