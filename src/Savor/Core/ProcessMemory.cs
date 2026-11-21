using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Savor.Core;

/// <summary>
/// A process's private memory, read straight from its handle. Process.PrivateMemorySize64 (after Refresh) takes a
/// snapshot of every process on the PC each time, which is far too costly for a once-a-second check.
/// </summary>
public static class ProcessMemory
{
    [StructLayout(LayoutKind.Sequential)]
    struct Counters
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                     QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(nint process, out Counters counters, uint size);

    /// <summary>Private bytes, or 0 if it can't be read.</summary>
    public static long Private(Process p)
    {
        try
        {
            return GetProcessMemoryInfo(p.Handle, out var c, (uint)Marshal.SizeOf<Counters>()) ? (long)c.PrivateUsage : 0;
        }
        catch { return 0; }
    }
}
