using Microsoft.Win32;

namespace ProTakipCallerBridge;

/// <summary>
/// Bridge entry point. Runs as a hidden WinForms app so we get the message
/// loop P/Invoke callbacks require, plus a NotifyIcon for user feedback.
///
/// Lifecycle:
///   1. Load config. If not paired → show PairDialog until user pairs.
///   2. Register HKCU Run key so we auto-start on next Windows login.
///   3. Hook cid.dll via CidInterop.SetEvents.
///   4. On every callback, marshal the phone number into ApiClient.IngestAsync.
///   5. Tray menu exposes Re-pair, Test (fake call), and Exit.
/// </summary>
internal static class Program
{
    private static BridgeConfig _cfg = null!;
    private static ApiClient _api = null!;
    private static NotifyIcon _tray = null!;
    private static SynchronizationContext _ui = null!;
    private static string? _lastDeviceSerial;
    private static DateTime _lastSignalAt = DateTime.MinValue;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        _cfg = BridgeConfig.Load();
        _api = new ApiClient(_cfg);
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        // Startup registration — auto-run on Windows login, per-user so
        // no admin prompt. Idempotent.
        RegisterAutoStart();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ProTakip Caller Id",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        UpdateTrayState();
        _tray.DoubleClick += (_, __) => ShowStatusBalloon();

        // If we aren't paired yet, block on the pair dialog before hooking
        // the DLL — no point listening for calls we can't forward.
        if (!_cfg.IsPaired)
        {
            using var dlg = new PairDialog(_api, _cfg);
            dlg.ShowDialog();
            UpdateTrayState();
        }

        // Hook the DLL regardless of pair state; if the user cancels the
        // dialog we still want the tray icon alive so they can retry.
        try
        {
            CidInterop.SetEvents(OnCallerId, OnSignal);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "cid.dll yüklenemedi: " + ex.Message +
                "\n\nBridge tray'de kalacak ama telefon çağrılarını algılayamaz. " +
                "Visual C++ 2010/2015 runtime kurulu olduğundan emin olun.",
                "ProTakip Caller Id",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Application.Run();
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
            msg = "app.protakip.com'daki Caller ID popup'ından 6 haneli kodu alın.";
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
        catch { /* ignore — default browser missing */ }
    }

    private static void Repair()
    {
        _cfg.Clear();
        UpdateTrayState();
        using var dlg = new PairDialog(_api, _cfg);
        dlg.ShowDialog();
        UpdateTrayState();
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
        // Callback may fire on a non-UI thread. Marshal the network call onto
        // the UI context so we can safely touch the NotifyIcon.
        _ui.Post(async _ =>
        {
            _lastDeviceSerial ??= deviceSerial;

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
            if (existing != exePath)
            {
                // Quote the path so paths with spaces (Program Files) work.
                key.SetValue("ProTakipCallerBridge", "\"" + exePath + "\"");
            }
        }
        catch
        {
            // Non-fatal — user may be on a locked-down box. Bridge still
            // works while running; it just won't start on login.
        }
    }
}
