using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Savor.Core;

/// <summary>
/// Captures the audio of one application (and its child processes) using Windows' process-loopback
/// API — the same mechanism as OBS's "Application Audio Capture". Needs Windows 10 2004 (build 19041) or newer.
/// Output is 32-bit float stereo at the requested sample rate.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    readonly int _sampleRate;
    AudioClient? _client;
    AudioCaptureClient? _capture;
    Thread? _thread;
    readonly ManualResetEvent _stop = new(false);
    readonly AutoResetEvent _ready = new(false);
    float[] _buf = Array.Empty<float>();

    public int ProcessId { get; }
    /// <summary>Receives interleaved float stereo samples (count = number of floats). Called on a capture thread.</summary>
    public event Action<float[], int>? DataAvailable;

    ProcessAudioCapture(int pid, int sampleRate)
    {
        ProcessId = pid;
        _sampleRate = sampleRate;
    }

    public static async Task<ProcessAudioCapture> StartAsync(int pid, int sampleRate)
    {
        var cap = new ProcessAudioCapture(pid, sampleRate);
        try
        {
            // The audio client has no cross-apartment proxy: create and use it only on MTA threads
            // (never the UI thread), otherwise every call fails with InvalidCastException.
            await Task.Run(async () =>
            {
                await cap.ActivateAsync().ConfigureAwait(false);
                cap.Begin();
                if (!cap._started.Wait(3000)) throw new TimeoutException("App audio capture didn't start.");
                if (cap._startError != null) throw cap._startError;
            }).ConfigureAwait(false);
            return cap;
        }
        catch
        {
            cap.Dispose();
            throw;
        }
    }

    async Task ActivateAsync()
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            TargetProcessId = (uint)ProcessId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE,
        };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, paramsPtr, false);
            var prop = new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = (uint)size, pBlobData = paramsPtr };
            Marshal.StructureToPtr(prop, propPtr, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, propPtr, handler, out _);
            Marshal.ThrowExceptionForHR(hr);
            var client = await handler.Task.ConfigureAwait(false);
            _client = new AudioClient(client);
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            200_000 /* 20 ms in 100-ns units */, 0, format, Guid.Empty);
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
    }

    readonly ManualResetEventSlim _started = new(false);
    Exception? _startError;

    void Begin()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = $"Savor app audio {ProcessId}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Loop()
    {
        var handles = new WaitHandle[] { _stop, _ready };
        try
        {
            _client!.Start();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _started.Set();
            ReleaseClient();
            return;
        }
        _started.Set();
        try
        {
            while (WaitHandle.WaitAny(handles, 200) != 0)
            {
                int packet;
                while ((packet = _capture!.GetNextPacketSize()) > 0)
                {
                    IntPtr ptr = _capture.GetBuffer(out int frames, out var flags);
                    int n = frames * 2;
                    if (_buf.Length < n) _buf = new float[n];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buf, 0, n);
                    else Marshal.Copy(ptr, _buf, 0, n);
                    _capture.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(_buf, n);
                }
            }
        }
        catch { /* app exited or device changed: the mixer re-attaches */ }
        finally { ReleaseClient(); } // on this (MTA) thread, where the client lives
    }

    void ReleaseClient()
    {
        try { _client?.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _capture = null;
    }

    public bool IsAlive => _thread is { IsAlive: true };

    public void Dispose()
    {
        _stop.Set();
        if (_thread != null) _thread.Join(1000);
        else if (_client != null) Task.Run(ReleaseClient).Wait(1000); // never started: release on an MTA thread
    }

    // ===================== finding apps =====================

    /// <summary>Apps that currently have an audio session on the default output device.</summary>
    public static List<(string Exe, string Title)> AppsWithAudio()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var pid = (int)sessions[i].GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    using var proc = Process.GetProcessById(pid);
                    var exe = proc.ProcessName + ".exe";
                    if (list.Any(x => x.Item1.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue;
                    var title = Describe(proc);
                    list.Add((exe, title));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Running apps that have a window (candidates even if they're silent right now).</summary>
    public static List<(string Exe, string Title)> AppsWithWindows()
    {
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                var exe = p.ProcessName + ".exe";
                if (list.Any(x => x.Item1.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add((exe, Describe(p)));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Describe(Process p)
    {
        try
        {
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc!;
        }
        catch { }
        return p.ProcessName;
    }

    /// <summary>
    /// The root process for an exe name (e.g. the main Discord.exe, not its helpers), so capturing its
    /// process tree gets all of the app's audio. Returns null if it isn't running.
    /// </summary>
    public static int? FindRootProcess(string exe)
    {
        var name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        var procs = Process.GetProcessesByName(name);
        try
        {
            if (procs.Length == 0) return null;
            var ids = procs.Select(p => p.Id).ToHashSet();
            foreach (var p in procs)
            {
                int parent = ParentId(p.Id);
                if (!ids.Contains(parent)) return p.Id; // its parent isn't the same app → root
            }
            return procs[0].Id;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    static int ParentId(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        finally { CloseHandle(h); }
    }

    // ===================== interop =====================

    enum AUDIOCLIENT_ACTIVATION_TYPE { DEFAULT = 0, PROCESS_LOOPBACK = 1 }
    enum PROCESS_LOOPBACK_MODE { INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE_TARGET_PROCESS_TREE = 1 }

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>IAgileObject marker: tells COM the handler may be called from any thread.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Task => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object obj);
                if (hr < 0) _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("Activation failed", hr));
                else _tcs.TrySetResult((IAudioClient)obj);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }
    }
}
