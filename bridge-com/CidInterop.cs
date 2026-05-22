using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ProTakipCallerBridgeCom
{
    /// <summary>
    /// P/Invoke wrapper around the cidshow SDK <c>cid.dll</c> for the OLD
    /// caller-ID devices (C812A / C814A). Mirrors the .NET 8 main bridge's
    /// CidInterop but targets net48/x86 — folder deployment, so cid.dll sits
    /// next to the exe and we don't need the .NET Core NativeLibrary resolver.
    ///
    /// Signature comes from the vendor Delphi source (Unit1.pas):
    ///   TCallerID = procedure(const DeviceSerial: PWideChar; ...) stdcall;
    /// → StdCall + LPWStr + Unicode. The vendor C# sample marshals as BSTR /
    /// Cdecl and crashes on the CallerID callback (silently dropping rings);
    /// the signal callback is integer-only so the CC mismatch was tolerated.
    /// These signatures match the Delphi source exactly.
    ///
    /// NOTE: this path only delivers CallerID for the OLD devices. CID v5/v6
    /// fire the Signal callback through cid.dll but NOT CallerID — those need
    /// the cidv5callerid ActiveX COM path (see MainForm "com" mode).
    /// </summary>
    internal static class CidInterop
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

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
        /// strengths — the "is the box plugged in?" heartbeat.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public delegate void SignalCallback(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceModel,
            [MarshalAs(UnmanagedType.LPWStr)] string deviceSerial,
            int signal1,
            int signal2,
            int signal3,
            int signal4);

        [DllImport("cid.dll", EntryPoint = "SetEvents",
            CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern void SetEventsNative(CallerIdCallback callerId, SignalCallback signal);

        // Roots — the DLL holds a raw pointer into the managed delegate's
        // unmanaged thunk; a GC'd delegate becomes a crash. Keep alive for
        // the process lifetime.
        private static CallerIdCallback _callerIdKeepAlive;
        private static SignalCallback _signalKeepAlive;
        private static bool _dllDirSet;

        public static void SetEvents(CallerIdCallback callerId, SignalCallback signal)
        {
            // cid.dll LoadLibrary's a secondary module for the ring event;
            // make sure the exe folder is on the DLL search path so it finds
            // the sibling DLLs even when launched from an odd working dir.
            if (!_dllDirSet)
            {
                try { SetDllDirectory(Application.StartupPath); } catch { /* best effort */ }
                _dllDirSet = true;
            }

            _callerIdKeepAlive = callerId;
            _signalKeepAlive = signal;
            SetEventsNative(callerId, signal);
        }
    }
}
