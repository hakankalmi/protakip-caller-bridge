using System.Reflection;
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
///
/// We ship as a single-file self-extracting exe. At first run .NET unpacks
/// content (including our two cid.dll copies) under AppContext.BaseDirectory
/// which equals the extract temp folder. DllImport paths are literal
/// strings that wouldn't normally survive self-extract; we register a
/// DllImportResolver that rewrites them to absolute paths under the extract
/// folder, which always works.
/// </summary>
public static class CidInterop
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
    private const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    static CidInterop()
    {
        // The DLL search-path problem, in plain language:
        //
        // Self-extracting single-file publish drops cid.dll under the
        // runtime extract folder (AppContext.BaseDirectory → %TEMP%\.net\...).
        // SetDllImportResolver handles loading cid.dll itself, but when
        // cid.dll needs to LoadLibrary a secondary module (caller-id
        // event module, TSR hook, whatever — signal callback works
        // without it, the ring event apparently does not), Windows
        // searches the usual places and *does not* include our extract
        // folder. LoadLibrary returns NULL → DLL can't hand us the
        // CallerID callback → rings are silently dropped.
        //
        // Fix: add the extract folder to the DLL search path both
        // legacy (SetDllDirectory) and modern (AddDllDirectory +
        // SetDefaultDllDirectories) styles. Covers every Windows version
        // we care about.

        var baseDir = AppContext.BaseDirectory;
        var cidX64 = Path.Combine(baseDir, "cidshow_x64");
        var cidX86 = Path.Combine(baseDir, "cidshow_x86");

        try
        {
            // Legacy: altername DLL dirs the loader scans after the exe
            // dir and System32. Still respected by LoadLibrary on every
            // supported Windows.
            SetDllDirectory(baseDir);

            // Modern: allowlist-style dirs for LoadLibraryEx. Combined
            // with SetDefaultDllDirectories these become the *only* dirs
            // scanned unless the caller opts in to others — safer and
            // bypasses PATH pollution on secretary PCs.
            try { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS); } catch { }
            try { AddDllDirectory(baseDir); } catch { }
            if (Directory.Exists(cidX64)) try { AddDllDirectory(cidX64); } catch { }
            if (Directory.Exists(cidX86)) try { AddDllDirectory(cidX86); } catch { }
        }
        catch { /* best effort — if these fail cid.dll still loads via the resolver */ }

        // Rewrite "cidshow_x64/cid.dll" and "cidshow_x86/cid.dll" to absolute
        // paths in the runtime extract folder. Registered once per process
        // — NativeLibrary remembers the mapping for subsequent calls.
        NativeLibrary.SetDllImportResolver(
            typeof(CidInterop).Assembly,
            (libraryName, _, _) =>
            {
                if (!libraryName.Contains("cid.dll", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                var normalized = libraryName.Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(AppContext.BaseDirectory, normalized);
                return File.Exists(full) ? NativeLibrary.Load(full) : IntPtr.Zero;
            });
    }


    // ── KRİTİK —  DLL imzası vendor C# örneğinden FARKLI. ────────────
    // Delphi source (Unit1.pas, vendor'un kendi Test.exe'sinin kaynağı):
    //
    //   TCallerID = procedure(const DeviceSerial: PWideChar; ...) stdcall;
    //
    // Yani:
    //   * Calling convention = stdcall  (Cdecl DEĞİL)
    //   * String tipi         = PWideChar / LPWStr  (BSTR DEĞİL)
    //   * Charset             = Unicode (UTF-16)     (Ansi DEĞİL)
    //
    // Vendor C# örneği (cidshow_CSharpAnyCPU) yanlış yazılmış; Delphi
    // Test.exe çalışıyor ama C# örneği bile çalışmıyor. Signal callback
    // integer parametre olduğu için calling-convention farkı tolere
    // ediliyordu, CallerID ise BSTR marshal sırasında crash → callback
    // sessiz düşüyordu. Bu imzalar Delphi source ile BİREBİR.

    /// <summary>Fires when the device detects an incoming call.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void CallerIdCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceSerial,
        [MarshalAs(UnmanagedType.LPWStr)] string line,
        [MarshalAs(UnmanagedType.LPWStr)] string phoneNumber,
        [MarshalAs(UnmanagedType.LPWStr)] string dateTime,
        [MarshalAs(UnmanagedType.LPWStr)] string other);

    /// <summary>
    /// Fires roughly every second with device presence + line signal
    /// strengths. Gives us the "Is the box plugged in?" heartbeat.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void SignalCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceModel,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceSerial,
        int signal1,
        int signal2,
        int signal3,
        int signal4);

    [DllImport("cidshow_x64/cid.dll", EntryPoint = "SetEvents",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern void SetEventsX64(CallerIdCallback callerId, SignalCallback signal);

    [DllImport("cidshow_x86/cid.dll", EntryPoint = "SetEvents",
        CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
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
