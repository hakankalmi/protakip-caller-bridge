using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// First-run pair screen — deliberately plain. Previous attempts stacked
/// a custom gradient header on top of docked + absolute children and the
/// DPI scaler ate half the geometry. This version:
///
///   - Uses the OS's native title bar (no custom header strip).
///   - Puts all content inside a single vertical <c>FlowLayoutPanel</c>
///     so every child measures itself and Windows handles DPI.
///   - No owner-drawn anything except the accent button.
///
/// Final visual:
///
///   ╔═ ProTakip Caller Id ═══════════════════╗
///   ║                                         ║
///   ║   Köprü Eşleştirme                      ║   ← 18pt bold, blue
///   ║                                         ║
///   ║   Sekreter bilgisayarınızı ProTakip     ║
///   ║   hesabınıza bağlıyorsunuz. app.         ║
///   ║   protakip.com'da sağ üstteki Caller    ║
///   ║   ID göstergesine tıklayın, "USB        ║
///   ║   Cihaz" seçip çıkan 6 haneli kodu      ║
///   ║   aşağıya girin.                         ║
///   ║                                         ║
///   ║   [  ][  ][  ][  ][  ][  ]              ║   ← 6 TextBoxes
///   ║                                         ║
///   ║   [       EŞLEŞTİR       ]              ║   ← big blue button
///   ║                                         ║
///   ║   status-line                            ║
///   ║   app.protakip.com'u aç →                ║
///   ╚═════════════════════════════════════════╝
/// </summary>
public class PairDialog : Form
{
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);   // blue-600
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);   // blue-800
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);    // slate-900
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);   // slate-600
    private static readonly Color Disabled      = Color.FromArgb(148, 163, 184); // slate-400
    private static readonly Color Success       = Color.FromArgb(22, 163, 74);   // green-600
    private static readonly Color ErrorColor    = Color.FromArgb(220, 38, 38);   // red-600
    private static readonly Color InputBg       = Color.FromArgb(248, 250, 252); // slate-50

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
        ClientSize = new Size(680, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10.25f);
        ShowInTaskbar = true;

        // A single vertical stack — every control lays itself out from top
        // to bottom with its own top-margin. No absolute positioning, no
        // docking overlap.
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Color.White,
            Padding = new Padding(56, 40, 56, 32),
        };
        Controls.Add(stack);

        // ── Title ──────────────────────────────────────────────────
        var title = new Label
        {
            Text = "Köprü Eşleştirme",
            AutoSize = true,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = AccentPrimary,
            Margin = new Padding(0, 0, 0, 12),
        };
        stack.Controls.Add(title);

        // ── Instruction paragraph ──────────────────────────────────
        var instr = new Label
        {
            Text = "Sekreter bilgisayarınızı ProTakip hesabınıza bağlıyorsunuz. "
                 + "app.protakip.com'da sağ üstteki Caller ID göstergesine tıklayın, "
                 + "\"USB Cihaz\" seçip çıkan 6 haneli eşleşme kodunu aşağıya girin.",
            AutoSize = true,
            MaximumSize = new Size(568, 0), // 680 - 56*2 = 568 → wraps inside padding
            Font = new Font("Segoe UI", 10.5f),
            ForeColor = TextStrong,
            Margin = new Padding(0, 0, 0, 24),
        };
        stack.Controls.Add(instr);

        // ── 6-digit code entry row ─────────────────────────────────
        var codeRow = BuildCodeEntry();
        codeRow.Margin = new Padding(0, 0, 0, 24);
        stack.Controls.Add(codeRow);

        // ── Pair button ────────────────────────────────────────────
        _pairBtn = new AccentButton
        {
            Text = "EŞLEŞTİR",
            Size = new Size(568, 52),
            Enabled = false,
            Margin = new Padding(0, 0, 0, 16),
        };
        _pairBtn.Click += async (_, __) => await DoPairAsync();
        stack.Controls.Add(_pairBtn);

        // ── Status label ───────────────────────────────────────────
        _statusLabel = new Label
        {
            AutoSize = false,
            Size = new Size(568, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextMuted,
            Text = "",
            Margin = new Padding(0, 0, 0, 8),
        };
        stack.Controls.Add(_statusLabel);

        // ── Open web link ──────────────────────────────────────────
        var linkWrap = new Panel
        {
            Size = new Size(568, 24),
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        var link = new LinkLabel
        {
            Text = "app.protakip.com'u aç",
            AutoSize = true,
            Font = new Font("Segoe UI", 10f),
            LinkColor = AccentPrimary,
            ActiveLinkColor = AccentPressed,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, __) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://app.protakip.com",
                    UseShellExecute = true,
                });
            }
            catch { /* browser not installed */ }
        };
        linkWrap.Controls.Add(link);
        linkWrap.Resize += (_, __) =>
        {
            link.Location = new Point((linkWrap.Width - link.Width) / 2, 0);
        };
        stack.Controls.Add(linkWrap);

        AcceptButton = _pairBtn;
        Load += (_, __) =>
        {
            _digits[0].Focus();
            // Force-centre link once everything is laid out.
            foreach (Control c in linkWrap.Controls) c.Location =
                new Point((linkWrap.Width - c.Width) / 2, 0);
        };
    }

    // ── Code entry row ──────────────────────────────────────────────

    private Panel BuildCodeEntry()
    {
        const int slotW = 78;
        const int slotH = 72;
        const int gap = 12;
        int totalW = 6 * slotW + 5 * gap;

        var row = new Panel
        {
            Size = new Size(568, slotH + 4),
            BackColor = Color.White,
        };

        int startX = (row.Width - totalW) / 2;

        for (int i = 0; i < 6; i++)
        {
            var box = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 1,
                BackColor = InputBg,
                ForeColor = AccentPrimary,
                Size = new Size(slotW, slotH),
                Location = new Point(startX + i * (slotW + gap), 0),
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
                else if (e.KeyCode == Keys.Right && idx < 5 &&
                         box.SelectionStart == box.Text.Length)
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
                _statusLabel.ForeColor = ErrorColor;
                _statusLabel.Text = "Kod geçersiz veya süresi dolmuş. Web panelden yeni kod alın.";
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
            _statusLabel.ForeColor = ErrorColor;
            _statusLabel.Text = "Bağlantı hatası: " + ex.Message;
            foreach (var d in _digits) d.Enabled = true;
            UpdateButtonState();
        }
    }

    protected override void OnClosing(CancelEventArgs e) => base.OnClosing(e);

    // ── Accent button (minimal owner-draw: background colour + text) ────

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

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg;
            Color fg = Color.White;
            if (!Enabled)       { bg = Disabled; fg = Color.WhiteSmoke; }
            else if (_down)     bg = AccentPressed;
            else if (_hover)    bg = AccentHover;
            else                bg = AccentPrimary;

            g.Clear(bg);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }
    }
}
