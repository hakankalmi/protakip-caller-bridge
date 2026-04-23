using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// First-run dialog: asks the user to paste the 6-digit code they saw in the
/// web panel's caller-id popup. Blocks the tray icon until the bridge is
/// paired — without a device token there's nowhere for <c>IngestAsync</c>
/// to send calls.
/// </summary>
public class PairDialog : Form
{
    private readonly ApiClient _api;
    private readonly BridgeConfig _cfg;

    private readonly TextBox _codeBox;
    private readonly Button _pairBtn;
    private readonly Label _statusLabel;

    public PairDialog(ApiClient api, BridgeConfig cfg)
    {
        _api = api;
        _cfg = cfg;

        Text = "ProTakip Caller Id — Eşleşme";
        Size = new Size(460, 280);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        var title = new Label
        {
            Text = "ProTakip Caller Id Köprüsü",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20),
            ForeColor = Color.FromArgb(17, 24, 39),
        };

        var sub = new Label
        {
            Text = "app.protakip.com'da gösterilen 6 haneli eşleşme kodunu girin.",
            AutoSize = true,
            Location = new Point(24, 52),
            ForeColor = Color.FromArgb(100, 116, 139),
            MaximumSize = new Size(410, 0),
        };

        _codeBox = new TextBox
        {
            Location = new Point(24, 92),
            Size = new Size(200, 40),
            Font = new Font("Consolas", 22, FontStyle.Bold),
            MaxLength = 6,
            TextAlign = HorizontalAlignment.Center,
            CharacterCasing = CharacterCasing.Upper,
        };
        _codeBox.KeyPress += (_, e) =>
        {
            // Only digits — mirrors the 6-digit numeric pair code shape.
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
        _codeBox.TextChanged += (_, __) => _pairBtn!.Enabled = _codeBox.Text.Trim().Length == 6;

        _pairBtn = new Button
        {
            Text = "Eşleştir",
            Location = new Point(236, 92),
            Size = new Size(180, 40),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Cursor = Cursors.Hand,
        };
        _pairBtn.FlatAppearance.BorderSize = 0;
        _pairBtn.Click += async (_, __) => await DoPairAsync();

        _statusLabel = new Label
        {
            Location = new Point(24, 150),
            Size = new Size(392, 60),
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = "",
        };

        var openWebLink = new LinkLabel
        {
            Text = "app.protakip.com'u aç",
            Location = new Point(24, 210),
            AutoSize = true,
            LinkColor = Color.FromArgb(37, 99, 235),
        };
        openWebLink.LinkClicked += (_, __) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://app.protakip.com",
                    UseShellExecute = true,
                });
            }
            catch { /* no-op — browser might be missing */ }
        };

        Controls.AddRange(new Control[] { title, sub, _codeBox, _pairBtn, _statusLabel, openWebLink });
        AcceptButton = _pairBtn;
    }

    private async Task DoPairAsync()
    {
        var code = _codeBox.Text.Trim();
        if (code.Length != 6) return;

        _pairBtn.Enabled = false;
        _codeBox.Enabled = false;
        _statusLabel.ForeColor = Color.FromArgb(100, 116, 139);
        _statusLabel.Text = "Sunucuya bağlanılıyor...";

        try
        {
            var res = await _api.ClaimAsync(code, deviceSerial: null);
            if (res == null)
            {
                _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
                _statusLabel.Text = "Kod geçersiz veya süresi dolmuş. Web panelde yeni bir kod alın.";
                _pairBtn.Enabled = true;
                _codeBox.Enabled = true;
                return;
            }

            _cfg.DeviceToken = res.DeviceToken;
            _cfg.CompanyId = res.CompanyId;
            _cfg.CompanyName = res.CompanyName;
            _cfg.DeviceId = res.DeviceId;
            _cfg.PairedAt = DateTime.UtcNow;
            _cfg.Save();

            _statusLabel.ForeColor = Color.FromArgb(22, 163, 74);
            _statusLabel.Text = $"Bağlantı kuruldu: {res.CompanyName}";
            await Task.Delay(1200);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
            _statusLabel.Text = "Bağlantı hatası: " + ex.Message;
            _pairBtn.Enabled = true;
            _codeBox.Enabled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Allow closing even without pairing — tray icon stays red and the
        // user can retry from "Eşleştir" menu item.
        base.OnClosing(e);
    }
}
