using Microsoft.Win32;

namespace ProTakipCallerBridge;

/// <summary>
/// Bridge entry point. Runs as a hidden WinForms app so we get the message
/// loop P/Invoke callbacks require, plus a NotifyIcon for user feedback.
///
/// Lifecycle:
///   1. Attach global error handlers + start logging to AppData.
///   2. Load config. If not paired → show PairDialog until user pairs.
///   3. Register HKCU Run key so we auto-start on next Windows login.
///   4. Hook cid.dll via CidInterop.SetEvents.
///   5. On every callback, marshal the phone number into ApiClient.IngestAsync.
///   6. Tray menu exposes Re-pair, Test (fake call), and Exit.
/// </summary>
internal static class Program
{
    private static BridgeConfig _cfg = null!;
    private static ApiClient _api = null!;
    private static NotifyIcon _tray = null!;
    private static SynchronizationContext _ui = null!;
    private static string? _lastDeviceSerial;
    private static DateTime _lastSignalAt = DateTime.MinValue;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProTakipCallerBridge");
    private static readonly string LogPath = Path.Combine(LogDir, "bridge.log");

    [STAThread]
    private static void Main()
    {
        // Log dir + top-level error handlers BEFORE anything else. A silent
        // startup crash was the #1 user complaint — now any exception writes
        // to bridge.log and pops a MessageBox so the user can see it.
        try { Directory.CreateDirectory(LogDir); } catch { /* best effort */ }
        Log("=== Bridge starting ===");
        Log($"Exe: {Environment.ProcessPath}");
        Log($"BaseDir: {AppContext.BaseDirectory}");
        Log($"OS: {Environment.OSVersion}  |  x64: {Environment.Is64BitProcess}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("UNHANDLED: " + e.ExceptionObject);
            try
            {
                MessageBox.Show(
                    $"Beklenmeyen hata:\n\n{e.ExceptionObject}\n\n" +
                    $"Log: {LogPath}",
                    "ProTakip Caller Id",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* ignore — may be called after message loop dies */ }
        };
        Application.ThreadException += (_, e) =>
        {
            Log("THREAD EXCEPTION: " + e.Exception);
            MessageBox.Show(
                $"Arayüz hatası:\n\n{e.Exception.Message}\n\n" +
                $"Detay log: {LogPath}",
                "ProTakip Caller Id",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();
            Log("App config initialized");

            _cfg = BridgeConfig.Load();
            _api = new ApiClient(_cfg);
            _ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ui);
            Log($"Config loaded — isPaired={_cfg.IsPaired} company={_cfg.CompanyName ?? "-"}");

            // Startup registration — auto-run on Windows login, per-user so
            // no admin prompt. Idempotent.
            RegisterAutoStart();

            _tray = new NotifyIcon
            {
                // SystemIcons.Application is a tiny generic icon — use the
                // blue "information" one so the user can actually spot it in
                // the hidden-icons flyout. Text appears as the hover tooltip.
                Icon = SystemIcons.Information,
                Text = "ProTakip Caller Id",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu(),
            };
            UpdateTrayState();
            _tray.DoubleClick += (_, __) => ShowStatusBalloon();
            Log("Tray icon created");

            // Windows 11 hides tray icons by default. Pop a balloon tip on
            // first launch so the user knows the bridge is running and can
            // find it in the system tray. Best-effort — Windows can still
            // suppress balloons in Focus Assist mode.
            _tray.ShowBalloonTip(
                6000,
                "ProTakip Caller Id çalışıyor",
                "Görev çubuğu sağ alttaki ^ okuna tıklayın ve bu simgeyi sabitleyin. Gelen aramalar buradan iletilecek.",
                ToolTipIcon.Info);

            // If we aren't paired yet, block on the pair dialog before hooking
            // the DLL — no point listening for calls we can't forward.
            if (!_cfg.IsPaired)
            {
                Log("Not paired — showing PairDialog");
                ShowPairDialog();
            }

            // Hook the DLL regardless of pair state; if the user cancels the
            // dialog we still want the tray icon alive so they can retry.
            try
            {
                CidInterop.SetEvents(OnCallerId, OnSignal);
                Log("cid.dll SetEvents hooked");
            }
            catch (Exception ex)
            {
                Log("cid.dll load FAILED: " + ex);
                MessageBox.Show(
                    "cid.dll yüklenemedi: " + ex.Message +
                    "\n\nBridge tray'de kalacak ama telefon çağrılarını algılayamaz. " +
                    "Visual C++ 2010/2015 runtime kurulu olduğundan emin olun.",
                    "ProTakip Caller Id",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Log("Entering message loop");
            Application.Run();
            Log("=== Bridge exited normally ===");
        }
        catch (Exception ex)
        {
            Log("FATAL startup error: " + ex);
            MessageBox.Show(
                $"Başlatma hatası:\n\n{ex.Message}\n\n" +
                $"Detaylı log: {LogPath}",
                "ProTakip Caller Id — Hata",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Tray ────────────────────────────────────────────────────────────

    private static ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Durumu göster", null, (_, __) => ShowStatusBalloon());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Web paneli aç", null, (_, __) => OpenWeb());
        menu.Items.Add("Test çağrısı gönder", null, async (_, __) => await SendTestCallAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Yeniden eşleştir", null, (_, __) => Repair());
        menu.Items.Add("Log dosyası", null, (_, __) => OpenLog());
        menu.Items.Add("Çıkış", null, (_, __) => Exit());
        return menu;
    }

    private static void UpdateTrayState()
    {
        if (_cfg.IsPaired)
        {
            _tray.Text = "ProTakip Caller Id — " + (_cfg.CompanyName ?? "Bağlı");
        }
        else
        {
            _tray.Text = "ProTakip Caller Id — Eşleşme bekleniyor";
        }
    }

    private static void ShowStatusBalloon()
    {
        string title, msg;
        ToolTipIcon icon;

        if (!_cfg.IsPaired)
        {
            title = "Eşleşme bekleniyor";
            msg = "app.protakip.com'daki Caller ID popup'ından 6 haneli kodu alın. Sağ tıklayıp 'Yeniden eşleştir'.";
            icon = ToolTipIcon.Warning;
        }
        else
        {
            title = _cfg.CompanyName ?? "Bağlı";
            var deviceMsg = _lastDeviceSerial != null
                ? $"Cihaz: {_lastDeviceSerial}"
                : "Cihaz bağlantısı bekleniyor";
            var signalMsg = _lastSignalAt > DateTime.MinValue
                ? $"Son sinyal: {(DateTime.UtcNow - _lastSignalAt).TotalSeconds:0}s önce"
                : "Henüz sinyal yok";
            msg = $"{deviceMsg}\n{signalMsg}";
            icon = ToolTipIcon.Info;
        }

        _tray.ShowBalloonTip(4000, title, msg, icon);
    }

    private static void OpenWeb()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://app.protakip.com",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log("OpenWeb failed: " + ex.Message); }
    }

    private static void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log("OpenLog failed: " + ex.Message); }
    }

    private static void Repair()
    {
        _cfg.Clear();
        UpdateTrayState();
        ShowPairDialog();
        UpdateTrayState();
    }

    /// <summary>
    /// Modal pair dialog — brought to front explicitly. When the bridge is
    /// launched from a SmartScreen "Yine de çalıştır" click, Windows doesn't
    /// always give it focus, so the dialog can open behind the browser and
    /// be invisible to the user. Topmost + Activate + bring to foreground
    /// fixes this.
    /// </summary>
    private static void ShowPairDialog()
    {
        try
        {
            using var dlg = new PairDialog(_api, _cfg);
            dlg.TopMost = true;
            dlg.Shown += (_, __) =>
            {
                dlg.Activate();
                dlg.BringToFront();
                dlg.TopMost = false; // don't stay always-on-top after first appearance
            };
            dlg.ShowDialog();
            Log($"PairDialog closed — isPaired now={_cfg.IsPaired}");
        }
        catch (Exception ex)
        {
            Log("PairDialog failed: " + ex);
            MessageBox.Show(
                "Eşleşme penceresi açılamadı: " + ex.Message,
                "ProTakip Caller Id",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Exit()
    {
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    // ── cid.dll callbacks ───────────────────────────────────────────────

    private static void OnCallerId(string deviceSerial, string line, string phoneNumber,
        string dateTime, string other)
    {
        _ui.Post(async _ =>
        {
            _lastDeviceSerial ??= deviceSerial;
            Log($"Incoming call {phoneNumber} line={line} serial={deviceSerial}");

            if (!_cfg.IsPaired)
            {
                _tray.ShowBalloonTip(3000, "Eşleşme yok",
                    $"Gelen arama: {phoneNumber} (eşleşmemiş — backend'e iletilmedi)",
                    ToolTipIcon.Warning);
                return;
            }

            try
            {
                var ok = await _api.IngestAsync(phoneNumber, line, deviceSerial, dateTime, other);
                if (ok)
                {
                    _tray.ShowBalloonTip(2500, "Gelen arama",
                        $"{phoneNumber}  (hat {line})", ToolTipIcon.Info);
                }
                else
                {
                    _tray.ShowBalloonTip(3500, "Iletilemedi",
                        "Backend 401 döndü — 'Yeniden eşleştir' gerekebilir.",
                        ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log("IngestAsync failed: " + ex.Message);
                _tray.ShowBalloonTip(3500, "Ağ hatası", ex.Message, ToolTipIcon.Error);
            }
        }, null);
    }

    private static void OnSignal(string deviceModel, string deviceSerial,
        int signal1, int signal2, int signal3, int signal4)
    {
        _lastSignalAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(deviceSerial))
            _lastDeviceSerial = deviceSerial;
    }

    // ── Manual test ─────────────────────────────────────────────────────

    private static async Task SendTestCallAsync()
    {
        if (!_cfg.IsPaired)
        {
            MessageBox.Show("Önce köprüyü eşleştirin.", "ProTakip Caller Id",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var ok = await _api.IngestAsync(
            phoneNumber: "05551112233",
            line: "TEST",
            deviceSerial: _lastDeviceSerial ?? "TEST-SERIAL",
            callAt: DateTime.UtcNow.ToString("O"),
            other: "bridge-manual-test");

        MessageBox.Show(
            ok
                ? "Test çağrısı gönderildi. Tarayıcıya düşmesi lazım."
                : "Gönderilemedi. Bağlantıyı kontrol edin.",
            "ProTakip Caller Id",
            MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ── Auto-start ──────────────────────────────────────────────────────

    private static void RegisterAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var existing = key.GetValue("ProTakipCallerBridge") as string;
            if (existing != "\"" + exePath + "\"")
            {
                key.SetValue("ProTakipCallerBridge", "\"" + exePath + "\"");
                Log("Auto-start registry key set");
            }
        }
        catch (Exception ex)
        {
            Log("Auto-start failed (non-fatal): " + ex.Message);
        }
    }

    // ── Logging ─────────────────────────────────────────────────────────

    private static readonly object _logLock = new();

    internal static void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch { /* logging failures are non-fatal */ }
    }
}
