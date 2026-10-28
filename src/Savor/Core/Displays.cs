using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Savor.Core;

public sealed record DisplayInfo(int OutputIndex, string Label, int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Enumerates monitors in the same order ffmpeg's ddagrab uses (outputs of the default DXGI adapter),
/// so "Display 2" in Savor is the screen ddagrab actually captures.
/// </summary>
public static class Displays
{
    public static List<DisplayInfo> Get()
    {
        try
        {
            var list = EnumerateDxgi();
            if (list.Count > 0) return list;
        }
        catch { }
        return FromScreens();
    }

    static List<DisplayInfo> FromScreens()
    {
        var screens = System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ToList();
        return screens.Select((s, i) => new DisplayInfo(i, MakeLabel(i, s.Bounds.Width, s.Bounds.Height, s.Primary),
            s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToList();
    }

    static string MakeLabel(int i, int w, int h, bool primary) =>
        $"Display {i + 1}  ·  {w}×{h}" + (primary ? "  (primary)" : "");

    static List<DisplayInfo> EnumerateDxgi()
    {
        var result = new List<DisplayInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return result;
        try
        {
            if (factory.EnumAdapters(0, out var adapter) != 0 || adapter == null) return result;
            try
            {
                for (uint i = 0; ; i++)
                {
                    if (adapter.EnumOutputs(i, out var output) != 0 || output == null) break;
                    try
                    {
                        if (output.GetDesc(out var d) != 0) continue;
                        int w = d.Right - d.Left, h = d.Bottom - d.Top;
                        bool primary = d.Left == 0 && d.Top == 0;
                        result.Add(new DisplayInfo((int)i, MakeLabel((int)i, w, h, primary), d.Left, d.Top, w, h, primary));
                    }
                    finally { Marshal.ReleaseComObject(output); }
                }
            }
            finally { Marshal.ReleaseComObject(adapter); }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return result;
    }

    // ---- minimal DXGI interop (vtable order matters; unused slots are placeholders) ----

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumAdapters(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }
}
