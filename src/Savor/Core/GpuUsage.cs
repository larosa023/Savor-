using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Savor.Core;

/// <summary>
/// How busy each GPU engine (3D, copy, video encode…) is for a given set of processes: the same Windows counters
/// Task Manager's GPU column uses ("GPU Engine" → "Utilization Percentage"). Counters are added for the processes'
/// IDs once, then each Sample() is a single cheap collect.
/// </summary>
public sealed class GpuUsage : IDisposable
{
    IntPtr _query;
    readonly List<IntPtr> _counters = new();
    bool _primed;

    public GpuUsage(IEnumerable<int> pids)
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            foreach (var pid in pids)
                if (PdhAddEnglishCounter(_query, $@"\GPU Engine(pid_{pid}_*)\Utilization Percentage", IntPtr.Zero, out var c) == 0)
                    _counters.Add(c);
            if (_counters.Count == 0) Dispose();
        }
        catch { Dispose(); }
    }

    /// <summary>Percent busy per engine type ("3D", "Copy", "VideoEncode"…), summed over the processes; null when
    /// unavailable (needs Windows 10 1709+ and a WDDM 2 driver) or on the first call (counters need two samples).</summary>
    public Dictionary<string, double>? Sample()
    {
        if (_query == IntPtr.Zero) return null;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            if (!_primed) { _primed = true; return null; }
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _counters)
            {
                uint size = 0, count = 0;
                int st = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
                if (st != PDH_MORE_DATA || size == 0) continue;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) continue;
                    int itemSize = Marshal.SizeOf<Item>();
                    for (int i = 0; i < count; i++)
                    {
                        var it = Marshal.PtrToStructure<Item>(buf + i * itemSize);
                        if (it.Status != 0) continue;
                        var name = Marshal.PtrToStringUni(it.Name) ?? "";
                        int k = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                        var type = k >= 0 ? name[(k + 8)..] : "Other";
                        result[type] = (result.TryGetValue(type, out var v) ? v : 0) + it.Value;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>"3D 2%, Copy 1%, VideoEncode 14%" (busiest engines first).</summary>
    public static string Describe(Dictionary<string, double>? u) =>
        u == null || u.Count == 0 ? "not available"
        : string.Join(", ", u.Where(x => x.Value >= 0.5 || x.Key is "3D" or "VideoEncode")
                             .OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:0}%"));

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { try { PdhCloseQuery(_query); } catch { } _query = IntPtr.Zero; }
    }

    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    struct Item
    {
        public IntPtr Name;
        public uint Status;
        public double Value; // PDH_FMT_COUNTERVALUE: CStatus, then the value union (8-byte aligned)
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}
