using System.Runtime.InteropServices;
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
    private static System.Threading.Timer? _heartbeatTimer;

    /// <summary>
    /// Active NetGSM TCP client when the paired company has Bulut Santral
    /// enabled. Null when the company is USB-only or not configured yet.
    /// Heartbeat re-polls <c>/caller-id/pbx-config</c>; if version changes
    /// or enabled flag flips we stop the old client and start a new one.
    /// </summary>
    private static NetgsmTcpClient? _netgsm;
    private static string? _netgsmVersion;
    private static string? _netgsmUsername;
    private static DateTime? _netgsmLastEventAt;

    /// <summary>
    /// Premium status window — opens on tray double-click and stays hidden
    /// otherwise. Created once so we can keep the live call feed across
    /// closes without losing history.
    /// </summary>
    private static StatusForm? _statusForm;
    private static bool _backendReachable = true;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProTakipCallerBridge");
    private static readonly string LogPath = Path.Combine(LogDir, "bridge.log");

    /// <summary>
    /// Bridge version string shown in the log header, StatusForm subtitle,
    /// and tray tooltip so Hakan can tell which build is running during
    /// deploy testing. Pulled from the compiled assembly
    /// (AssemblyInformationalVersion or fallback to FileVersion) — csproj
    /// and GitHub Actions stamp it at build time with run_number.
    /// </summary>
    internal static string AppVersion { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        if (info.Length > 0)
        {
            var raw = ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            // Strip SourceLink "+commit" suffix if present.
            var plus = raw.IndexOf('+');
            return plus > 0 ? raw[..plus] : raw;
        }
        return asm.GetName().Version?.ToString() ?? "dev";
    }

    [STAThread]
    private static void Main()
    {
        // Log dir + top-level error handlers BEFORE anything else. A silent
        // startup crash was the #1 user complaint — now any exception writes
        // to bridge.log and pops a MessageBox so the user can see it.
        try { Directory.CreateDirectory(LogDir); } catch { /* best effort */ }
        Log("=== Bridge starting ===");
        Log($"Version: {AppVersion}");
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
                // Starts pending (amber) — flips to green when paired +
                // heartbeat succeeds, flips to red on persistent failure.
                Icon = BuildTrayIcon(TrayState.Pending),
                Text = "ProTakip Caller Id",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu(),
            };
            UpdateTrayState();
            _tray.DoubleClick += (_, __) => ShowStatusWindow();
            Log("Tray icon created");

            // Windows 11 hides tray icons by default. Pop a balloon tip on
            // first launch so the user knows the bridge is running and can
            // find it in the system tray. Best-effort — Windows can still
            // suppress balloons in Focus Assist mode.
            _tray.ShowBalloonTip(
                6000,
                "ProTakip Caller Id çalışıyor",
                "Durum penceresi için simgeye çift tıklayın. Görev çubuğundaki ^ okuna tıklayıp sabitlemeniz önerilir.",
                ToolTipIcon.Info);

            // Lazily build the status window — constructed once so the
            // call-feed + timestamps survive hide/show cycles. Not Shown()
            // here; user pops it via double-click.
            _statusForm = new StatusForm(_cfg);
            _statusForm.TestCallRequested += async (_, _) => await SendTestCallAsync();
            _statusForm.RepairRequested   += (_, _) => Repair();
            _statusForm.OpenLogRequested  += (_, _) => OpenLog();
            _statusForm.UpdateCompany(_cfg.CompanyName);
            _statusForm.UpdateUsb(connected: false, deviceSerial: null, lastSignalAt: null);
            _statusForm.UpdateNetgsm(NetgsmState.Disabled);

            // Force-create the window handle so a message-pumping HWND
            // exists before we hook cid.dll. CIDSHOW's native callbacks
            // require a message-loop window on the calling thread; if
            // we hook with no window the DLL never fires events (OnSignal
            // also stays silent). The sample Form1.cs hooks inside
            // Form_Load after the handle exists — same trick here.
            _ = _statusForm.Handle;
            Log("Status form HWND allocated — DLL callbacks will have a message pump");

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

            // Heartbeat — every 60s push /caller-id/ping so the web header
            // indicator stays green during idle periods (no calls for hours).
            // Also reconciles the NetGSM Bulut Santral TCP subscription: if
            // the company enabled PBX since the last tick (or changed creds)
            // we start / restart the socket here. First tick after 5s so a
            // freshly-paired bridge comes online quickly.
            _heartbeatTimer = new System.Threading.Timer(async _ =>
            {
                if (!_cfg.IsPaired) return;
                bool pingOk = false;
                try
                {
                    var (ok, detail) = await _api.PingAsync();
                    pingOk = ok;
                    if (!ok) Log("Ping failed — " + detail);
                }
                catch (Exception ex) { Log("Ping threw: " + ex.Message); }

                // Tray icon reflects backend reachability — amber while
                // eşleşme bekleniyor, green on success, red if pings keep
                // failing. Debounced via `_backendReachable` so a single
                // blip doesn't flash the icon red.
                if (_backendReachable != pingOk)
                {
                    _backendReachable = pingOk;
                    _ui?.Post(_ => UpdateTrayState(), null);
                }

                // Best-effort PBX reconcile — any error just keeps the
                // previous state alive.
                try { await ReconcileNetgsmAsync(); }
                catch (Exception ex) { Log("Netgsm reconcile threw: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
            Log("Heartbeat timer armed (5s initial, 60s interval)");

            Log("Entering message loop");
            Application.Run();
            _heartbeatTimer.Dispose();
            _netgsm?.Dispose();
            _statusForm?.Dispose();
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
        var menu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
        };
        menu.Items.Add("Durum penceresini aç", null, (_, __) => ShowStatusWindow());
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
        string titleSuffix;
        TrayState state;

        if (!_cfg.IsPaired)
        {
            titleSuffix = "Eşleşme bekleniyor";
            state = TrayState.Pending;
        }
        else if (!_backendReachable)
        {
            titleSuffix = "Sunucuya ulaşılamıyor";
            state = TrayState.Error;
        }
        else
        {
            titleSuffix = _cfg.CompanyName ?? "Bağlı";
            state = TrayState.Ok;
        }

        // Tray tooltip max 63 chars on Win10; version kısaltılıp gösteriliyor.
        _tray.Text = $"ProTakip Caller Id v{AppVersion} — {titleSuffix}";
        _tray.Icon?.Dispose();
        _tray.Icon = BuildTrayIcon(state);
    }

    /// <summary>
    /// Tray icon states. Windows tray is 16x16 @ 100% DPI, scales to 20/24
    /// at higher DPIs. We draw a filled circle on a translucent square so
    /// the color is legible at any size.
    /// </summary>
    private enum TrayState { Ok, Pending, Error }

    private static Icon BuildTrayIcon(TrayState state)
    {
        var color = state switch
        {
            TrayState.Ok      => Color.FromArgb(22, 163, 74),   // green-600
            TrayState.Pending => Color.FromArgb(217, 119, 6),   // amber-600
            TrayState.Error   => Color.FromArgb(220, 38, 38),   // red-600
            _ => Color.Gray,
        };

        // 32x32 source — Windows downscales cleanly. Phone glyph inside a
        // solid circle so the icon reads at 16px.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 28, 28);

            // Simple phone glyph — offset handset shape on circle center.
            using var pen = new Pen(Color.White, 2.4f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            // Earpiece + mouthpiece with a curve between — stylized ☏.
            g.DrawArc(pen, 9, 9, 14, 14, 135, 270);
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void ShowStatusWindow()
    {
        if (_statusForm == null) return;
        try
        {
            if (!_statusForm.Visible) _statusForm.Show();
            if (_statusForm.WindowState == FormWindowState.Minimized)
                _statusForm.WindowState = FormWindowState.Normal;
            _statusForm.Activate();
            _statusForm.BringToFront();
            _statusForm.TopMost = true;
            _statusForm.TopMost = false;
        }
        catch (Exception ex)
        {
            Log("ShowStatusWindow failed: " + ex.Message);
        }
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
        _statusForm?.UpdateCompany(null);
        UpdateTrayState();
        ShowPairDialog();
        _statusForm?.UpdateCompany(_cfg.CompanyName);
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
                _statusForm?.AddRecentCall(phoneNumber, "usb", success: false);
                _tray.ShowBalloonTip(3000, "Eşleşme yok",
                    $"Gelen arama: {phoneNumber} (eşleşmemiş — backend'e iletilmedi)",
                    ToolTipIcon.Warning);
                return;
            }

            try
            {
                var ok = await _api.IngestAsync(phoneNumber, line, deviceSerial, dateTime, other, source: "usb");
                _statusForm?.AddRecentCall(phoneNumber, "usb", ok);
                if (!ok)
                {
                    // Sadece başarısızlıkta balon patlat — başarılı çağrıda
                    // tarayıcıda zaten dock açılıyor, iki yerde bildirim
                    // sekreterin ekranını karıştırmasın.
                    _tray.ShowBalloonTip(3500, "İletilemedi",
                        "Backend 401 döndü — 'Yeniden eşleştir' gerekebilir.",
                        ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log("IngestAsync failed: " + ex.Message);
                _statusForm?.AddRecentCall(phoneNumber, "usb", success: false);
                _tray.ShowBalloonTip(3500, "Ağ hatası", ex.Message, ToolTipIcon.Error);
            }
        }, null);
    }

    private static bool _firstSignalLogged;

    private static void OnSignal(string deviceModel, string deviceSerial,
        int signal1, int signal2, int signal3, int signal4)
    {
        _lastSignalAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(deviceSerial))
            _lastDeviceSerial = deviceSerial;

        // Ilk sinyali loga yaz — DLL callback'inin ATEŞLENDIGINI KANITLAMAK
        // debugging icin kritik. Daha sonra tek tek loglamiyoruz, spam olur.
        if (!_firstSignalLogged)
        {
            _firstSignalLogged = true;
            Log($"First DLL Signal received — model={deviceModel} serial={deviceSerial} s1={signal1} s2={signal2} s3={signal3} s4={signal4}");
        }

        // USB sinyali geldiğinde status penceresine yansıt. Sinyal her 5-10
        // saniyede bir geliyor — UI'ı yormasın diye sadece değişiklikte
        // güncelleme mantığı StatusForm içinde (label compare).
        _ui?.Post(_ =>
        {
            _statusForm?.UpdateUsb(connected: true,
                deviceSerial: _lastDeviceSerial,
                lastSignalAt: _lastSignalAt);
        }, null);
    }

    // ── NetGSM Bulut Santral (parallel to USB) ──────────────────────────

    /// <summary>
    /// Pull current PBX config from the backend, then start/stop/restart the
    /// local NetGSM TCP subscriber as needed. USB and NetGSM run side-by-side
    /// — both source paths land at the same /caller-id/ingest endpoint, just
    /// with different <c>source</c> tags.
    /// </summary>
    private static async Task ReconcileNetgsmAsync()
    {
        var cfg = await _api.GetPbxConfigAsync();

        // Network/auth glitch — keep whatever we have running rather than
        // tearing down a healthy socket over a transient error.
        if (cfg == null) return;

        // Company turned PBX off (or it was never on) — stop any running
        // subscription and clear state.
        if (!cfg.Enabled ||
            string.IsNullOrWhiteSpace(cfg.Username) ||
            string.IsNullOrWhiteSpace(cfg.Password) ||
            string.IsNullOrWhiteSpace(cfg.Host))
        {
            if (_netgsm != null)
            {
                Log("Netgsm disabled on server — stopping subscriber");
                _netgsm.Dispose();
                _netgsm = null;
                _netgsmVersion = null;
                _netgsmUsername = null;
                _netgsmLastEventAt = null;
            }
            _ui?.Post(_ => _statusForm?.UpdateNetgsm(NetgsmState.Disabled), null);
            return;
        }

        // Already running with the same credentials — nothing to do.
        if (_netgsm != null && _netgsmVersion == cfg.Version) return;

        // Credentials changed (or first start) — tear down + restart with
        // the new ones. NetGSM can take a second or two to accept a new
        // login, but we don't block the UI thread on this.
        _netgsm?.Dispose();
        _netgsm = new NetgsmTcpClient(
            host: cfg.Host!,
            port: cfg.Port > 0 ? cfg.Port : 9110,
            username: cfg.Username!,
            password: cfg.Password!,
            version: cfg.Version ?? string.Empty,
            onIncomingNumber: OnNetgsmIncomingAsync,
            log: Log);
        _netgsm.Start();
        _netgsmVersion = cfg.Version;
        _netgsmUsername = cfg.Username;
        _netgsmLastEventAt = null;
        Log($"Netgsm subscriber started — version={cfg.Version}");
        _ui?.Post(_ =>
        {
            _statusForm?.UpdateNetgsm(NetgsmState.Connected, cfg.Username, _netgsmLastEventAt);
        }, null);
    }

    /// <summary>
    /// Callback from <see cref="NetgsmTcpClient"/> on every inbound ring.
    /// Posts to the same /caller-id/ingest endpoint USB uses, tagged with
    /// <c>source="netgsm"</c> so the backend / browser dock can tell where
    /// the event came from.
    /// </summary>
    private static async Task OnNetgsmIncomingAsync(string phoneNumber)
    {
        if (!_cfg.IsPaired) return;

        try
        {
            var ok = await _api.IngestAsync(
                phoneNumber: phoneNumber,
                line: null,
                deviceSerial: null,
                callAt: DateTime.UtcNow.ToString("O"),
                other: "netgsm-tcp",
                source: "netgsm");

            _netgsmLastEventAt = DateTime.UtcNow;
            _ui?.Post(_ =>
            {
                _statusForm?.AddRecentCall(phoneNumber, "netgsm", ok);
                _statusForm?.UpdateNetgsm(NetgsmState.Connected, _netgsmUsername, _netgsmLastEventAt);
            }, null);

            if (!ok) Log("Netgsm ingest returned non-success");
        }
        catch (Exception ex)
        {
            Log("Netgsm ingest threw: " + ex.Message);
            _ui?.Post(_ => _statusForm?.AddRecentCall(phoneNumber, "netgsm", success: false), null);
        }
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

        // Ask the user for a phone number so they can test both the
        // "kayıtlı müşteri" and the "kayıtsız müşteri" flows. Previous
        // version hard-coded 05551112233 which only ever hit the
        // unknown-caller path.
        using var dlg = new TestCallDialog();
        dlg.TopMost = true;
        dlg.Shown += (_, __) => { dlg.Activate(); dlg.BringToFront(); dlg.TopMost = false; };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var phone = dlg.PhoneNumber;
        if (string.IsNullOrWhiteSpace(phone)) return;

        var ok = await _api.IngestAsync(
            phoneNumber: phone,
            line: "TEST",
            deviceSerial: _lastDeviceSerial ?? "TEST-SERIAL",
            callAt: DateTime.UtcNow.ToString("O"),
            other: "bridge-manual-test");

        if (!ok)
        {
            MessageBox.Show(
                "Gönderilemedi. Bağlantıyı kontrol edin.",
                "ProTakip Caller Id",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // Başarılı durumda dialog zaten kapalı; web panelde sağ üstte kart
        // patlayacak, ek bir bildirim kullanıcıyı rahatsız etmesin diye
        // MessageBox göstermiyoruz.
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
