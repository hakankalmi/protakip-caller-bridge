using System.Runtime.InteropServices;

namespace ProTakipCallerBridge;

/// <summary>
/// P/Invoke wrapper around the vendor <c>cid.dll</c> (CIDSHOW SDK). The DLL
/// ships in two flavours — <c>cidshow_x64/cid.dll</c> and
/// <c>cidshow_x86/cid.dll</c> — that export the same <c>SetEvents</c>
/// function. The implementation picks the right one at runtime based on
/// <c>IntPtr.Size</c>. Callback delegates must stay rooted for the process
/// lifetime (<see cref="_callerIdKeepAlive"/>/<see cref="_signalKeepAlive"/>)
/// — the DLL keeps native references and a GC'd delegate becomes a crash.
/// </summary>
public static class CidInterop
{
    /// <summary>Fires when the device detects an incoming call.</summary>
    public delegate void CallerIdCallback(
        [MarshalAs(UnmanagedType.BStr)] string deviceSerial,
        [MarshalAs(UnmanagedType.BStr)] string line,
        [MarshalAs(UnmanagedType.BStr)] string phoneNumber,
        [MarshalAs(UnmanagedType.BStr)] string dateTime,
        [MarshalAs(UnmanagedType.BStr)] string other);

    /// <summary>
    /// Fires roughly every second with device presence + line signal
    /// strengths. Gives us the "Is the box plugged in?" heartbeat.
    /// </summary>
    public delegate void SignalCallback(
        [MarshalAs(UnmanagedType.BStr)] string deviceModel,
        [MarshalAs(UnmanagedType.BStr)] string deviceSerial,
        int signal1,
        int signal2,
        int signal3,
        int signal4);

    [DllImport("cidshow_x64/cid.dll", EntryPoint = "SetEvents",
        CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void SetEventsX64(CallerIdCallback callerId, SignalCallback signal);

    [DllImport("cidshow_x86/cid.dll", EntryPoint = "SetEvents",
        CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void SetEventsX86(CallerIdCallback callerId, SignalCallback signal);

    // Roots — never let these get GC'd after SetEvents returns. The DLL
    // holds a raw pointer into the managed delegate's unmanaged thunk;
    // the Form1 sample leaks them for the same reason.
#pragma warning disable IDE0052
    private static CallerIdCallback? _callerIdKeepAlive;
    private static SignalCallback? _signalKeepAlive;
#pragma warning restore IDE0052

    public static void SetEvents(CallerIdCallback callerId, SignalCallback signal)
    {
        _callerIdKeepAlive = callerId;
        _signalKeepAlive = signal;

        if (IntPtr.Size == 8)
            SetEventsX64(callerId, signal);
        else
            SetEventsX86(callerId, signal);
    }
}
