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
    /// Last NetGSM state we surfaced to the user (tray balloon). Lets us
    /// fire the "Yanlış kullanıcı adı veya parola" balloon ONCE per state
    /// transition into AuthRejected — without this, the AuthRejected retry
    /// cycle (300s) would balloon the secretary every 5 minutes for as long
    /// as credentials stay wrong.
    /// </summary>
    private static NetgsmConnectionState? _lastNetgsmStateNotified;

    /// <summary>
    /// Premium status window — opens on tray double-click and stays hidden
    /// otherwise. Created once so we can keep the live call feed across
    /// closes without losing history.
    /// </summary>
    private static StatusForm? _statusForm;
    private static bool _backendReachable = true;
    private static int _consecutive401;

    /// <summary>
    /// Heartbeat tick counter. Used to throttle non-critical periodic work
    /// (auto-update check) so it runs every N pings instead of on every one.
    /// </summary>
    private static int _heartbeatTicks;

    /// <summary>
    /// Latest update info from <c>/caller-id/latest-version</c>, populated by
    /// the heartbeat timer. Drives the "Güncelleştirmeyi indir" tray menu item
    /// and the one-time update balloon notification.
    /// </summary>
    private static UpdateInfo? _latestUpdate;
    private static bool _updateBalloonShown;
    private static ToolStripMenuItem? _updateMenuItem;

    /// <summary>
    /// Reusable callback delegates so Repair() / ApplyHiddenActivate() can
    /// rebind the StatusForm Activated event without leaking handler
    /// references across re-creations. Hooked once in Main().
    /// </summary>
    private static bool _cidHookCompleted;
    private static bool _hiddenActivateApplied;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ProTakipCallerBridge");
    private static readonly string LogPath = Path.Combine(LogDir, "bridge.log");

    /// <summary>
    /// Log rotation thresholds. 5 MB per file × 5 backups = 25 MB max history.
    /// Adequate for ~30 days of typical bridge traffic at one log line per
    /// NetGSM frame + heartbeat noise.
    /// </summary>
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private const int MaxLogBackups = 5;

    /// <summary>
    /// Cheap counter to avoid calling <see cref="FileInfo.Length"/> on every
    /// log line. We check size every 100 lines — at ~150 bytes/line that's
    /// ~15 KB between checks, well below the 5 MB rotation threshold.
    /// </summary>
    private static int _logLinesSinceRotateCheck;

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

            // Vendor Form1.cs line 118 — DLL callback'leri UI thread
            // dışından gelince WinForms cross-thread exception fırlatıyor
            // ve CallerID event'i drop olabiliyor. Disable ederek vendor
            // davranışıyla birebir yap.
            System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = false;
            Log("App config initialized");

            _cfg = BridgeConfig.Load();
            _api = new ApiClient(_cfg);
            _ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ui);
            Log($"Config loaded — isPaired={_cfg.IsPaired} company={_cfg.CompanyName ?? "-"}");

            // SELF-HEAL — primary config has no token but a backup exists
            // (most common cause: previous bridge built with the Repair()
            // Clear bug overwrote a healthy pair; #1 in the audit). Try the
            // backup token against /caller-id/ping; if the server still
            // recognises it, promote the backup to primary and skip the
            // pair dialog entirely. No user intervention required.
            if (!_cfg.IsPaired)
            {
                TrySelfHealFromBackup();
            }

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

            // Hook cid.dll INSIDE StatusForm's ACTIVATED event — vendor
            // Delphi source (Unit1.pas):
            //   procedure TForm1.FormActivate(Sender: TObject);
            //   begin
            //     SetEvents(CallerID, Signal);
            //   end;
            // Activated event Load'dan sonra fire eder, form VISIBLE +
            // foreground + odaklanmış durumda. DLL callback'i register
            // eden thread'i kaydettiği an window aktif olmalı. Load'da
            // form henüz visible değil → DLL CallerID event post'u
            // düşüyor olabilir. Activated'a taşıyoruz + ilk fire'da
            // tekrar hook etmeyecek şekilde tek seferlik.
            //
            // After the hook succeeds we IMMEDIATELY hide the form so the
            // secretary doesn't see it pop up on every boot (#4 in the
            // audit). The form starts minimized + ShowInTaskbar=false, gets
            // activated once for the hook, then disappears. Tray double-
            // click brings it back via ShowStatusWindow().
            _statusForm.Activated += (_, _) =>
            {
                if (_cidHookCompleted) return;

                try
                {
                    var callerIdDelegate = new CidInterop.CallerIdCallback(OnCallerId);
                    var signalDelegate = new CidInterop.SignalCallback(OnSignal);
                    CidInterop.SetEvents(callerIdDelegate, signalDelegate);
                    _cidHookCompleted = true;
                    Log("cid.dll SetEvents hooked (inside Activated event — Delphi vendor pattern)");
                }
                catch (Exception ex)
                {
                    Log("cid.dll load FAILED: " + ex);
                    MessageBox.Show(
                        "cid.dll yüklenemedi: " + ex.Message +
                        "\n\nBridge tray'de kalacak ama telefon çağrılarını algılayamaz.",
                        "ProTakip Caller Id",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _cidHookCompleted = true; // don't retry on every activation
                }

                // Drop the form back to tray. BeginInvoke so we don't fight
                // with the in-flight Activated handler — Hide() while inside
                // the event sometimes leaves WinForms in a weird focus state.
                if (!_hiddenActivateApplied)
                {
                    _hiddenActivateApplied = true;
                    _statusForm!.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _statusForm!.Hide();
                            // Restore for the NEXT show (via tray) — taskbar
                            // entry should appear when the user actively opens
                            // the window, just not at boot.
                            _statusForm.WindowState = FormWindowState.Normal;
                            _statusForm.ShowInTaskbar = true;
                        }
                        catch (Exception ex)
                        {
                            Log("StatusForm hide-after-hook failed: " + ex.Message);
                        }
                    }));
                }
            };

            // Boot in hidden mode. Application.Run(_statusForm) calls Show()
            // internally, which fires Activated (needed for the cid.dll
            // hook above). With Minimized + ShowInTaskbar=false the user
            // sees nothing — no popup, no taskbar icon, just the tray. The
            // Activated handler then Hide()'s the form so subsequent tray
            // clicks open it cleanly.
            _statusForm.WindowState = FormWindowState.Minimized;
            _statusForm.ShowInTaskbar = false;

            // If we aren't paired yet, block on the pair dialog before
            // starting the message loop. Pair finishes → Application.Run
            // launches StatusForm → Load fires → SetEvents hooks DLL.
            if (!_cfg.IsPaired)
            {
                Log("Not paired — showing PairDialog");
                ShowPairDialog();
            }

            // Heartbeat — every 60s push /caller-id/ping so the web header
            // indicator stays green during idle periods (no calls for hours).
            // Also reconciles the NetGSM Bulut Santral TCP subscription: if
            // the company enabled PBX since the last tick (or changed creds)
            // we start / restart the socket here. First tick after 5s so a
            // freshly-paired bridge comes online quickly.
            _heartbeatTimer = new System.Threading.Timer(async _ =>
            {
                _heartbeatTicks++;

                // Auto-update check — every 30 ticks (≈30 min at 60s heartbeat).
                // Runs even when unpaired so brand-new installations get the
                // latest-version balloon before the user even pairs. 404s are
                // silently swallowed by CheckLatestVersionAsync; nothing
                // happens if the backend hasn't shipped the endpoint yet.
                if (_heartbeatTicks % 30 == 1)
                {
                    try { await CheckForUpdatesAsync(); }
                    catch (Exception ex) { Log("Update check threw: " + ex.Message); }
                }

                if (!_cfg.IsPaired) return;
                bool pingOk = false;
                string? pingDetail = null;
                try
                {
                    var (ok, detail) = await _api.PingAsync();
                    pingOk = ok;
                    pingDetail = detail;
                    if (!ok) Log("Ping failed — " + detail);
                }
                catch (Exception ex)
                {
                    Log("Ping threw: " + ex.Message);
                    pingDetail = ex.Message;
                }

                // Tray icon reflects backend reachability — amber while
                // eşleşme bekleniyor, green on success, red if pings keep
                // failing. Debounced via `_backendReachable` so a single
                // blip doesn't flash the icon red.
                if (_backendReachable != pingOk)
                {
                    _backendReachable = pingOk;
                    _ui?.Post(_ => UpdateTrayState(), null);
                }

                // 401 is special — it means our device token is no longer
                // accepted (pair row deleted, token reset, whatever). No
                // amount of retry will recover; user needs to re-pair.
                // Surface a tray balloon on the first 401 so they know.
                if (!pingOk && (pingDetail?.Contains("401") ?? false))
                {
                    _consecutive401++;
                    if (_consecutive401 == 1)
                    {
                        _ui?.Post(_ =>
                        {
                            _tray?.ShowBalloonTip(
                                8000,
                                "Eşleşme geçersiz",
                                "Sunucu bu köprüyü tanımıyor. Tray simgesine sağ tıklayıp 'Yeniden eşleştir' seçin.",
                                ToolTipIcon.Warning);
                        }, null);
                    }
                }
                else if (pingOk)
                {
                    _consecutive401 = 0;
                }

                // Best-effort PBX reconcile — any error just keeps the
                // previous state alive.
                try { await ReconcileNetgsmAsync(); }
                catch (Exception ex) { Log("Netgsm reconcile threw: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
            Log("Heartbeat timer armed (5s initial, 60s interval)");

            Log("Entering message loop");
            // StatusForm'u parametre olarak veriyoruz — vendor sample
            // Application.Run(new Form1()) ile birebir. Bu pattern Form
            // Load event'inin fire etmesini garanti ediyor, SetEvents
            // Load içinde çalışıyor. Parametresiz Application.Run() Form
            // Load'unu hiç tetiklemezdi.
            Application.Run(_statusForm);
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

        // Update item — hidden by default; CheckForUpdatesAsync flips
        // Visible=true the moment a newer version is announced. Keeping
        // it in the menu (vs. injecting on demand) avoids tray menu
        // re-build races and gives the user a stable place to find the
        // download even after they dismiss the balloon.
        _updateMenuItem = new ToolStripMenuItem("Güncelleştirmeyi indir",
            null, (_, __) => OpenUpdateUrl())
        {
            Visible = false,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235), // blue-600
        };
        menu.Items.Add(_updateMenuItem);

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
            // Restore taskbar visibility on user-initiated open. Boot path
            // explicitly hides it (ShowInTaskbar=false in Main + hide-after-
            // hook in Activated); without this re-set the form would open
            // without a taskbar entry every subsequent time.
            _statusForm.ShowInTaskbar = true;
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
        // CONFIRM — Berkay Halı Yıkama vakası: önceki sürüm tek tıkla
        // pair'i siliyordu (Repair → _cfg.Clear → kullanıcı dialog'u
        // kapatınca pair gitti, 3 saat çağrı kaybı). Yanlışlıkla tıklama
        // veri kaybı yapmasın. Default button = No → ENTER yanlış
        // bastığında da sorun olmaz.
        if (_cfg.IsPaired)
        {
            var result = MessageBox.Show(
                $"\"{_cfg.CompanyName ?? "Mevcut hesap"}\" ile eşleşme kaldırılacak " +
                "ve yeni 6 haneli eşleşme kodu girmeniz istenecek.\n\n" +
                "Yeniden eşleştirmek istediğinizden emin misiniz?",
                "ProTakip Caller Id — Yeniden eşleştir",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
        }

        // CRITICAL — NO _cfg.Clear() HERE. PairDialog.DoPairAsync zaten
        // sadece BAŞARILI pair'de _cfg.DeviceToken'ı set ediyor + Save().
        // Kullanıcı dialog'u kapatırsa eski pair AYNEN KALSIN — Berkay
        // vakasının kök sebebi bu çağrıydı. Backup file da el değmemiş
        // şekilde duruyor (BridgeConfig.Save IsPaired'ken yazıyor).
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
        // SYNC log on the native callback thread — proves the DLL fired
        // the callback at all, independent of UI marshalling. If this
        // line never appears in bridge.log after a real call, the DLL
        // is not invoking the callback for this device/driver combo,
        // no amount of C# code changes will help.
        Log($"[native OnCallerId fired] phone='{phoneNumber}' line='{line}' serial='{deviceSerial}' dt='{dateTime}' other='{other}'");

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
    private static string? _loggedSerial;
    private static string? _loggedModel;

    private static void OnSignal(string deviceModel, string deviceSerial,
        int signal1, int signal2, int signal3, int signal4)
    {
        _lastSignalAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(deviceSerial))
            _lastDeviceSerial = deviceSerial;

        // İlk sinyali loga yaz — DLL callback'inin ateşlendiğini kanıtlar.
        if (!_firstSignalLogged)
        {
            _firstSignalLogged = true;
            Log($"First DLL Signal received — model='{deviceModel}' serial='{deviceSerial}' s1={signal1} s2={signal2} s3={signal3} s4={signal4}");
        }

        // Model veya seri değişirse log — tipik olarak ilk Signal boş
        // model/serial geliyor, cihaz USB üstünden enumerate edildikten
        // sonra dolu geliyor. Bu anı log'da görmek bağlantı aşamasını
        // takip etmek için kritik.
        if (deviceModel != _loggedModel || deviceSerial != _loggedSerial)
        {
            _loggedModel = deviceModel;
            _loggedSerial = deviceSerial;
            var hasDevice = !string.IsNullOrWhiteSpace(deviceSerial) || !string.IsNullOrWhiteSpace(deviceModel);
            Log(hasDevice
                ? $"Device identity changed — model='{deviceModel}' serial='{deviceSerial}' (hat sinyalleri: {signal1}/{signal2}/{signal3}/{signal4})"
                : "Device identity cleared — no model or serial from DLL");
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

        // Subscribe BEFORE Start() — RunAsync raises Connecting at the very
        // top of its loop, and a missed first transition leaves the status
        // card showing the previous run's state (or "Yapılandırılmadı").
        _netgsm.StateChanged += OnNetgsmStateChanged;

        _netgsmVersion = cfg.Version;
        _netgsmUsername = cfg.Username;
        _netgsmLastEventAt = null;
        // Fresh client → fresh notification state. Otherwise a credential
        // bump (server-side fix) wouldn't re-balloon if the new credentials
        // are ALSO wrong, because the previous AuthRejected balloon flag
        // would suppress it.
        _lastNetgsmStateNotified = null;

        _netgsm.Start();
        Log($"Netgsm subscriber started — version={cfg.Version}");

        // Show "Bağlanıyor…" immediately so the secretary doesn't see a
        // stale card while the TCP dial is in flight. The client will
        // publish Connected once the login succeeds, AuthRejected if
        // credentials are wrong, or Disconnected on transient failures.
        _ui?.Post(_ => _statusForm?.UpdateNetgsm(NetgsmState.Connecting, cfg.Username), null);
    }

    /// <summary>
    /// Bridges <see cref="NetgsmTcpClient.StateChanged"/> events to the
    /// status form + tray. Always called on the client's worker thread —
    /// we marshal to the UI thread here so neither the client nor the
    /// status form has to know about WinForms threading rules.
    /// </summary>
    private static void OnNetgsmStateChanged(NetgsmConnectionState state, string? detail)
    {
        _ui?.Post(_ =>
        {
            var stateTransitioned = _lastNetgsmStateNotified != state;
            _lastNetgsmStateNotified = state;

            switch (state)
            {
                case NetgsmConnectionState.Connecting:
                    _statusForm?.UpdateNetgsm(NetgsmState.Connecting, _netgsmUsername);
                    break;

                case NetgsmConnectionState.Connected:
                    _statusForm?.UpdateNetgsm(NetgsmState.Connected, _netgsmUsername, _netgsmLastEventAt);
                    break;

                case NetgsmConnectionState.AuthRejected:
                    _statusForm?.UpdateNetgsm(NetgsmState.Error, _netgsmUsername, null,
                        detail ?? "Yanlış kullanıcı adı veya parola");

                    // Balloon ONLY on the first transition into AuthRejected.
                    // The 300s retry would otherwise nag the secretary every
                    // 5 minutes for as long as the credentials stay wrong.
                    // A fresh credential change (ReconcileNetgsmAsync ->
                    // new client) resets _lastNetgsmStateNotified so the
                    // next failure WILL balloon again.
                    if (stateTransitioned)
                    {
                        _tray?.ShowBalloonTip(8000,
                            "NetGSM kullanıcı adı veya parola hatalı",
                            "Yönetici Paneli → Caller ID → Sanal Santral'da bilgileri güncelleyin.",
                            ToolTipIcon.Warning);
                    }
                    break;

                case NetgsmConnectionState.Disconnected:
                    _statusForm?.UpdateNetgsm(NetgsmState.Error, _netgsmUsername, _netgsmLastEventAt,
                        detail ?? "Bağlantı kesildi, yeniden deneniyor");
                    break;
            }
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

    // ── Self-heal ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to restore pair state from <c>config.backup.json</c> when the
    /// primary config has no token. The backup is only trusted after a live
    /// <c>/caller-id/ping</c> succeeds — a stale backup (server-side pair
    /// deleted, account removed, token revoked) would otherwise trick the
    /// bridge into thinking it's paired while every ingest 401s.
    ///
    /// Called from <see cref="Main"/> right after loading config. Synchronous
    /// (uses <c>.GetAwaiter().GetResult()</c>) because at this point the UI
    /// hasn't started — there's no thread to deadlock with, and we want the
    /// restore decision settled before tray icon / pair dialog logic runs.
    /// </summary>
    private static void TrySelfHealFromBackup()
    {
        BridgeConfig? backup;
        try { backup = BridgeConfig.LoadBackup(); }
        catch (Exception ex) { Log("Self-heal: backup load threw — " + ex.Message); return; }

        if (backup == null || !backup.IsPaired)
        {
            Log("Self-heal: no usable backup");
            return;
        }

        Log($"Self-heal: backup found (company={backup.CompanyName}), validating with server…");

        // Probe the server with the backup token via a throwaway ApiClient.
        // We don't reuse _api because it's bound to _cfg (no token yet).
        var probe = new ApiClient(backup);
        bool serverAccepts;
        try
        {
            var (ok, detail) = probe.PingAsync().GetAwaiter().GetResult();
            serverAccepts = ok;
            if (!ok) Log("Self-heal: server rejected backup token — " + detail);
        }
        catch (Exception ex)
        {
            // Network unreachable etc. — leave bridge unpaired; user can
            // retry next launch when connectivity returns, backup file is
            // untouched.
            Log("Self-heal: ping threw — " + ex.Message);
            return;
        }

        if (!serverAccepts) return;

        // Server still recognises the token — promote backup to primary.
        // AdoptFrom calls Save(), which atomically rewrites both files.
        _cfg.AdoptFrom(backup);
        Log($"Self-heal: pair otomatik geri yüklendi (company={_cfg.CompanyName})");
    }

    // ── Auto-update ────────────────────────────────────────────────────

    /// <summary>
    /// Polls <c>/caller-id/latest-version</c> and surfaces a tray balloon +
    /// menu item when a newer build is available. Comparison is via
    /// <see cref="Version"/> parsing (semantic-ish four-part); a malformed
    /// or older returned version is ignored. The balloon fires once per
    /// bridge process lifetime; the menu item stays visible until restart
    /// so the user can find the download even after dismissing the popup.
    /// </summary>
    private static async Task CheckForUpdatesAsync()
    {
        var latest = await _api.CheckLatestVersionAsync();
        if (latest == null || string.IsNullOrEmpty(latest.Version)) return;
        if (!IsNewerVersion(latest.Version, AppVersion)) return;

        _latestUpdate = latest;
        Log($"Update available: v{latest.Version} (current v{AppVersion})");

        _ui?.Post(_ =>
        {
            if (_updateMenuItem != null)
            {
                _updateMenuItem.Text = $"Güncelleştirmeyi indir (v{latest.Version})";
                _updateMenuItem.Visible = true;
            }

            if (!_updateBalloonShown)
            {
                _updateBalloonShown = true;
                _tray?.ShowBalloonTip(10000,
                    $"Yeni güncelleme: v{latest.Version}",
                    "Tray simgesine sağ tıklayıp 'Güncelleştirmeyi indir' seçin.",
                    ToolTipIcon.Info);
            }
        }, null);
    }

    private static bool IsNewerVersion(string? candidate, string? current)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(current)) return false;
        if (!Version.TryParse(candidate, out var c)) return false;
        if (!Version.TryParse(current, out var cur)) return false;
        return c > cur;
    }

    private static void OpenUpdateUrl()
    {
        var url = _latestUpdate?.DownloadUrl;
        if (string.IsNullOrEmpty(url))
        {
            // Fallback — user clicked the menu item but somehow we lost
            // the download URL. Send them to the web panel where the
            // Caller ID popup always has the latest link.
            url = "https://app.protakip.com";
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log("OpenUpdateUrl failed: " + ex.Message); }
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

                // Cheap throttle — FileInfo.Length is fast but we don't
                // want it on every line. Check once every 100 writes;
                // at ~150 bytes/line that's a 15 KB window, far below
                // the 5 MB rotation threshold.
                _logLinesSinceRotateCheck++;
                if (_logLinesSinceRotateCheck >= 100)
                {
                    _logLinesSinceRotateCheck = 0;
                    try
                    {
                        var fi = new FileInfo(LogPath);
                        if (fi.Length > MaxLogSizeBytes) RotateLogs();
                    }
                    catch { /* fs glitch — let it grow, retry next check */ }
                }
            }
        }
        catch { /* logging failures are non-fatal */ }
    }

    /// <summary>
    /// Rotates <c>bridge.log</c> when it exceeds <see cref="MaxLogSizeBytes"/>.
    /// Shifts <c>.4 → .5</c>, <c>.3 → .4</c>, … <c>bridge.log → bridge.log.1</c>;
    /// the oldest (<c>.5</c>) is discarded. Best-effort: file locks from a
    /// concurrent reader (the user has the log open in Notepad) just skip
    /// the rotation this round.
    /// </summary>
    private static void RotateLogs()
    {
        // Shift suffixed backups outward — .4→.5, .3→.4, .2→.3, .1→.2.
        for (int i = MaxLogBackups - 1; i >= 1; i--)
        {
            var src = $"{LogPath}.{i}";
            var dst = $"{LogPath}.{i + 1}";
            if (!File.Exists(src)) continue;
            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            catch { /* one busy file shouldn't abort the whole rotation */ }
        }

        // Active log → .1
        try
        {
            var first = $"{LogPath}.1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(LogPath, first);
        }
        catch { /* if the active log is locked we'll just keep appending */ }
    }
}
