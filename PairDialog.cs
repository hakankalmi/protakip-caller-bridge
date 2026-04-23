using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// First-run pair screen. Simple, clean WinForms layout — no owner-drawn
/// controls, no overlapping docked/absolute children (which was what made
/// the previous version look like two dialogs glued together). Everything
/// lives inside a root <see cref="TableLayoutPanel"/> so Windows handles
/// DPI scaling and nothing needs manual coordinates.
///
///   ┌──────────────────────────────────────────────┐
///   │            [ solid-blue header ]             │
///   │          ProTakip Caller Id                  │
///   │          Köprü eşleştirme                    │
///   ├──────────────────────────────────────────────┤
///   │                                               │
///   │   Sekreter PC'nizi ProTakip hesabınıza       │
///   │   bağlıyorsunuz. app.protakip.com'da sağ     │
///   │   üstteki Caller ID göstergesine tıklayın,   │
///   │   "USB Cihaz" seçip çıkan 6 haneli kodu      │
///   │   aşağıya girin.                              │
///   │                                               │
///   │   ┌──┬──┬──┬──┬──┬──┐                        │
///   │   │  │  │  │  │  │  │   ← 6 ayrı TextBox    │
///   │   └──┴──┴──┴──┴──┴──┘                        │
///   │                                               │
///   │   ┌────────────────────────────┐             │
///   │   │         EŞLEŞTİR            │             │
///   │   └────────────────────────────┘             │
///   │                                               │
///   │   status line                                │
///   │   app.protakip.com'u aç →                    │
///   └──────────────────────────────────────────────┘
/// </summary>
public class PairDialog : Form
{
    // Color palette — matches the web panel so the bridge feels like the
    // same product. Keep simple: one accent blue + one text gray.
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);   // blue-600
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);   // blue-800
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);    // slate-900
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);   // slate-600
    private static readonly Color TextLight     = Color.FromArgb(148, 163, 184); // slate-400
    private static readonly Color Success       = Color.FromArgb(22, 163, 74);   // green-600
    private static readonly Color Error         = Color.FromArgb(220, 38, 38);   // red-600
    private static readonly Color BorderSoft    = Color.FromArgb(203, 213, 225); // slate-300

    private readonly ApiClient _api;
    private readonly BridgeConfig _cfg;

    private readonly TextBox[] _digits = new TextBox[6];
    private readonly AccentButton _pairBtn;
    private readonly Label _statusLabel;

    public PairDialog(ApiClient api, BridgeConfig cfg)
    {
        _api = api;
        _cfg = cfg;

        Text = "ProTakip Caller Id";
        ClientSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Font;

        // ── Root table: 4 rows (header / body / actions / footer) ──
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.White,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // body (instruction + inputs)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // button + status
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // footer spacer + link
        Controls.Add(root);

        // ── Row 0: header ──────────────────────────────────────────
        root.Controls.Add(BuildHeader(), 0, 0);

        // ── Row 1: instruction + code entry ────────────────────────
        var body = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(56, 24, 56, 0),
            BackColor = Color.White,
        };
        body.Controls.Add(BuildInstructionLabel());
        body.Controls.Add(BuildCodeEntry());
        root.Controls.Add(body, 0, 1);

        // ── Row 2: pair button ─────────────────────────────────────
        _pairBtn = new AccentButton
        {
            Text = "Eşleştir",
            Size = new Size(420, 52),
            Anchor = AnchorStyles.None,
            Enabled = false,
            Margin = new Padding(56, 8, 56, 4),
        };
        _pairBtn.Click += async (_, __) => await DoPairAsync();
        var btnHost = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.White,
        };
        btnHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btnHost.Controls.Add(_pairBtn, 0, 0);
        root.Controls.Add(btnHost, 0, 2);

        // ── Row 3: status + link at bottom ─────────────────────────
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5f),
            Text = "",
        };
        footer.Controls.Add(_statusLabel, 0, 0);

        var linkHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var openWebLink = new LinkLabel
        {
            Text = "app.protakip.com'u aç",
            AutoSize = true,
            LinkColor = AccentPrimary,
            ActiveLinkColor = AccentPressed,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = new Font("Segoe UI", 9.5f),
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
            catch { /* no default browser */ }
        };
        linkHost.Controls.Add(openWebLink);
        linkHost.Resize += (_, __) =>
        {
            openWebLink.Location = new Point(
                (linkHost.Width - openWebLink.Width) / 2,
                linkHost.Height - openWebLink.Height - 24);
        };
        footer.Controls.Add(linkHost, 0, 1);

        root.Controls.Add(footer, 0, 3);

        AcceptButton = _pairBtn;
        Load += (_, __) => _digits[0].Focus();
    }

    // ── Header ──────────────────────────────────────────────────────

    private static Panel BuildHeader()
    {
        // Solid dark-blue strip (no gradient, no owner-drawing). Two labels
        // stacked inside with generous padding on top so the headline
        // doesn't collide with the window titlebar.
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = AccentPrimary };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = AccentPrimary,
            Padding = new Padding(48, 28, 48, 0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "ProTakip Caller Id",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        var subtitle = new Label
        {
            Text = "Köprü eşleştirme",
            Font = new Font("Segoe UI", 11f),
            ForeColor = Color.FromArgb(219, 234, 254), // blue-100
            AutoSize = true,
        };
        stack.Controls.Add(title, 0, 0);
        stack.Controls.Add(subtitle, 0, 1);
        panel.Controls.Add(stack);
        return panel;
    }

    // ── Body: instruction + code entry ──────────────────────────────

    private Label BuildInstructionLabel()
    {
        return new Label
        {
            Text = "Sekreter bilgisayarınızı ProTakip hesabınıza bağlıyorsunuz.\n"
                 + "app.protakip.com'da sağ üstteki Caller ID göstergesine tıklayın, "
                 + "\"USB Cihaz\" seçip çıkan 6 haneli eşleşme kodunu aşağıya girin.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 72,
            ForeColor = TextStrong,
            Font = new Font("Segoe UI", 10.25f),
        };
    }

    private Panel BuildCodeEntry()
    {
        // Six individual TextBoxes centred on a row. One box per digit is
        // kilometres more robust than fake painted dividers over a single
        // TextBox — Ctrl+V still works via a paste handler on the first box.
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 88,
            BackColor = Color.White,
        };

        var slotW = 68;
        var slotH = 72;
        var gap = 10;
        var totalW = 6 * slotW + 5 * gap;
        var startX = (608 - totalW) / 2; // 608 = body inner width (720 - 56*2)

        for (int i = 0; i < 6; i++)
        {
            var box = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 28f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 1,
                BackColor = Color.FromArgb(248, 250, 252), // slate-50
                ForeColor = AccentPrimary,
                Size = new Size(slotW, slotH),
                Location = new Point(startX + i * (slotW + gap), 8),
                Tag = i,
            };
            int idx = i;
            box.KeyPress += (_, e) =>
            {
                if (char.IsControl(e.KeyChar)) return;
                if (!char.IsDigit(e.KeyChar)) { e.Handled = true; return; }
            };
            box.TextChanged += (_, __) =>
            {
                if (box.Text.Length == 1 && idx < 5) _digits[idx + 1].Focus();
                UpdateButtonState();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Back && box.Text.Length == 0 && idx > 0)
                {
                    _digits[idx - 1].Focus();
                    _digits[idx - 1].SelectAll();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Left && idx > 0 && box.SelectionStart == 0)
                {
                    _digits[idx - 1].Focus();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Right && idx < 5 && box.SelectionStart == box.Text.Length)
                {
                    _digits[idx + 1].Focus();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.V)
                {
                    var clip = Clipboard.GetText()?.Trim() ?? "";
                    var digits = new string(clip.Where(char.IsDigit).Take(6).ToArray());
                    if (digits.Length > 0)
                    {
                        for (int k = 0; k < _digits.Length; k++)
                            _digits[k].Text = k < digits.Length ? digits[k].ToString() : "";
                        _digits[Math.Min(digits.Length, 5)].Focus();
                        e.SuppressKeyPress = true;
                        UpdateButtonState();
                    }
                }
            };
            _digits[i] = box;
            row.Controls.Add(box);
        }
        return row;
    }

    private string CodeText()
    {
        var sb = new System.Text.StringBuilder(6);
        foreach (var d in _digits) sb.Append(d.Text);
        return sb.ToString();
    }

    private void UpdateButtonState()
    {
        _pairBtn.Enabled = CodeText().Length == 6;
    }

    // ── Pair action ─────────────────────────────────────────────────

    private async Task DoPairAsync()
    {
        var code = CodeText();
        if (code.Length != 6) return;

        _pairBtn.Enabled = false;
        foreach (var d in _digits) d.Enabled = false;

        _statusLabel.ForeColor = TextMuted;
        _statusLabel.Text = "Sunucuya bağlanılıyor…";

        try
        {
            var res = await _api.ClaimAsync(code, deviceSerial: null);
            if (res == null)
            {
                _statusLabel.ForeColor = Error;
                _statusLabel.Text = "Kod geçersiz veya süresi dolmuş. Web panelde yeni kod alın.";
                foreach (var d in _digits) { d.Enabled = true; d.Text = ""; }
                _digits[0].Focus();
                UpdateButtonState();
                return;
            }

            _cfg.DeviceToken = res.DeviceToken;
            _cfg.CompanyId = res.CompanyId;
            _cfg.CompanyName = res.CompanyName;
            _cfg.DeviceId = res.DeviceId;
            _cfg.PairedAt = DateTime.UtcNow;
            _cfg.Save();

            _statusLabel.ForeColor = Success;
            _statusLabel.Text = $"Bağlantı kuruldu · {res.CompanyName}";
            await Task.Delay(1100);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Error;
            _statusLabel.Text = "Bağlantı hatası: " + ex.Message;
            foreach (var d in _digits) d.Enabled = true;
            UpdateButtonState();
        }
    }

    protected override void OnClosing(CancelEventArgs e) => base.OnClosing(e);

    // ───────────────────────────────────────────────────────────────
    //  Nested: owner-drawn button — flat blue with hover/press tints.
    // ───────────────────────────────────────────────────────────────

    private sealed class AccentButton : Button
    {
        private bool _hover;
        private bool _down;

        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = AccentPrimary;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Refresh(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Refresh(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Refresh(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Refresh(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg;
            if (!Enabled)       bg = BorderSoft;
            else if (_down)     bg = AccentPressed;
            else if (_hover)    bg = AccentHover;
            else                bg = AccentPrimary;

            g.Clear(bg);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle,
                Enabled ? Color.White : Color.FromArgb(100, 116, 139),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
