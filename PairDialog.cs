using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// First-run pair screen. Previous version was <c>FormBorderStyle.None</c>
/// +  <c>WindowState.Maximized</c> with a 720px center column, and Windows
/// DPI scaling (125% / 150%) turned the 36pt title into something that
/// wrapped mid-word. Now it's a proper fixed-size modal with <c>AutoScaleMode.Dpi</c>
/// so every control rescales cleanly — no manual pixel juggling, no word wraps.
///
/// Layout (560 × 620 at 100% DPI):
///
///   ┌─ ProTakip Caller Id ──────────────────────────────┐
///   │                                                    │
///   │   Köprü Eşleştirme                                │  ← 22pt bold blue
///   │   Sekreter bilgisayarını ProTakip hesabına         │  ← 10pt muted
///   │   bağlayın — tek seferlik.                         │
///   │                                                    │
///   │   ┌─ Nasıl yapılır? ──────────────────────────┐   │  ← blue info card
///   │   │ 1  app.protakip.com'u açın                │   │
///   │   │ 2  Sağ üstteki Caller ID göstergesine    │   │
///   │   │     tıklayın → USB Cihaz                  │   │
///   │   │ 3  Çıkan 6 haneli kodu aşağıya girin     │   │
///   │   └──────────────────────────────────────────┘   │
///   │                                                    │
///   │   Eşleşme Kodu                                     │  ← 9.5pt bold
///   │   [  ][  ][  ][  ][  ][  ]                        │  ← 6 × 56×72
///   │                                                    │
///   │   [         EŞLEŞTİR         ]                    │  ← full-width
///   │                                                    │
///   │           durum satırı                             │
///   │           app.protakip.com'u aç                    │  ← link
///   └────────────────────────────────────────────────────┘
/// </summary>
public class PairDialog : Form
{
    // Palette — same tokens as StatusForm + web panel for visual
    // continuity between all three surfaces.
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);   // blue-600
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);   // blue-800
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);    // slate-900
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);   // slate-600
    private static readonly Color TextFaint     = Color.FromArgb(148, 163, 184); // slate-400
    private static readonly Color Disabled      = Color.FromArgb(203, 213, 225); // slate-300
    private static readonly Color Success       = Color.FromArgb(22, 163, 74);   // green-600
    private static readonly Color ErrorColor    = Color.FromArgb(220, 38, 38);   // red-600
    private static readonly Color InputBg       = Color.FromArgb(248, 250, 252); // slate-50
    private static readonly Color InputBorder   = Color.FromArgb(226, 232, 240); // slate-200
    private static readonly Color InfoBg        = Color.FromArgb(239, 246, 255); // blue-50
    private static readonly Color InfoBorder    = Color.FromArgb(191, 219, 254); // blue-200
    private static readonly Color InfoText      = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color Surface       = Color.White;

    private readonly ApiClient _api;
    private readonly BridgeConfig _cfg;

    private readonly TextBox[] _digits = new TextBox[6];
    private readonly AccentButton _pairBtn;
    private readonly Label _statusLabel;

    public PairDialog(ApiClient api, BridgeConfig cfg)
    {
        _api = api;
        _cfg = cfg;

        // Proper modal, not fullscreen. 100% DPI = 560×620. AutoScaleMode.Dpi
        // lets Windows stretch fonts/controls proportionally on 125%/150%
        // monitors without content clipping or word-wrap.
        Text = "ProTakip Caller Id — Köprü Eşleştirme";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(560, 620);
        BackColor = Surface;
        Font = new Font("Segoe UI", 9.75f);
        ShowInTaskbar = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        int contentLeft = 36;
        int contentWidth = ClientSize.Width - contentLeft * 2;

        // ── Title ───────────────────────────────────────────────────
        var title = new Label
        {
            Text = "Köprü Eşleştirme",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = AccentPrimary,
            AutoSize = true,
            Location = new Point(contentLeft, 28),
            BackColor = Color.Transparent,
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Sekreter bilgisayarını ProTakip hesabına bağlayın — tek seferlik.",
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextMuted,
            AutoSize = false,
            Size = new Size(contentWidth, 20),
            Location = new Point(contentLeft, 68),
            BackColor = Color.Transparent,
        };
        Controls.Add(subtitle);

        // ── Info card (numbered steps) ──────────────────────────────
        var infoCard = new InfoCard
        {
            Location = new Point(contentLeft, 104),
            Size = new Size(contentWidth, 124),
        };
        Controls.Add(infoCard);

        // ── Code entry ──────────────────────────────────────────────
        var codeLabel = new Label
        {
            Text = "EŞLEŞME KODU",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = TextFaint,
            AutoSize = true,
            Location = new Point(contentLeft, 248),
            BackColor = Color.Transparent,
        };
        Controls.Add(codeLabel);

        var codeRow = BuildCodeEntry(contentLeft, contentWidth);
        codeRow.Location = new Point(contentLeft, 274);
        Controls.Add(codeRow);

        // ── Pair button ─────────────────────────────────────────────
        _pairBtn = new AccentButton
        {
            Text = "EŞLEŞTİR",
            Size = new Size(contentWidth, 48),
            Location = new Point(contentLeft, 376),
            Enabled = false,
        };
        _pairBtn.Click += async (_, _) => await DoPairAsync();
        Controls.Add(_pairBtn);

        // ── Status label ────────────────────────────────────────────
        _statusLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextMuted,
            Text = string.Empty,
            Size = new Size(contentWidth, 22),
            Location = new Point(contentLeft, 438),
            BackColor = Color.Transparent,
        };
        Controls.Add(_statusLabel);

        // ── Footer link ─────────────────────────────────────────────
        var link = new LinkLabel
        {
            Text = "app.protakip.com'u aç",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            LinkColor = AccentPrimary,
            ActiveLinkColor = AccentPressed,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
        };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://app.protakip.com",
                    UseShellExecute = true,
                });
            }
            catch { /* no browser installed */ }
        };
        Controls.Add(link);
        // Centre the link horizontally — position once after the label
        // measures itself (autosize), re-measure if DPI scales mid-life.
        void CenterLink() => link.Location = new Point(
            (ClientSize.Width - link.Width) / 2,
            ClientSize.Height - 36);
        Load += (_, _) => CenterLink();
        SizeChanged += (_, _) => CenterLink();

        AcceptButton = _pairBtn;
        Load += (_, _) => _digits[0].Focus();
    }

    // ── Code entry row ──────────────────────────────────────────────

    private Panel BuildCodeEntry(int containerLeft, int containerWidth)
    {
        const int slotW = 56;
        const int slotH = 72;
        const int gap = 12;
        int totalW = 6 * slotW + 5 * gap;

        var row = new Panel
        {
            Size = new Size(containerWidth, slotH + 4),
            BackColor = Color.Transparent,
        };

        int startX = (containerWidth - totalW) / 2;

        for (int i = 0; i < 6; i++)
        {
            var box = new DigitBox
            {
                Font = new Font("Segoe UI", 22f, FontStyle.Bold),
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
                if (!char.IsDigit(e.KeyChar)) { e.Handled = true; }
            };
            box.TextChanged += (_, _) =>
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
                foreach (var d in _digits) { d.Enabled = true; d.Text = string.Empty; }
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

    // ── Numbered-steps info card ────────────────────────────────────

    private sealed class InfoCard : Panel
    {
        public InfoCard()
        {
            BackColor = InfoBg;
            DoubleBuffered = true;
            Padding = new Padding(18, 14, 18, 14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Header
            using var headerFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var headerBrush = new SolidBrush(InfoText);
            g.DrawString("NASIL YAPILIR?", headerFont, headerBrush, 18f, 14f);

            using var stepFont = new Font("Segoe UI", 9.75f);
            using var stepBrush = new SolidBrush(TextStrong);
            using var numFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var numBrush = new SolidBrush(InfoText);
            using var numBg = new SolidBrush(Color.FromArgb(219, 234, 254)); // blue-100

            var steps = new[]
            {
                "app.protakip.com'u açın",
                "Caller ID göstergesine tıklayın → USB Cihaz",
                "Ekranda çıkan 6 haneli kodu aşağıya girin",
            };

            int y = 38;
            for (int i = 0; i < steps.Length; i++)
            {
                // Numbered circle
                var circleRect = new Rectangle(18, y, 20, 20);
                g.FillEllipse(numBg, circleRect);
                var numStr = (i + 1).ToString();
                var numSize = g.MeasureString(numStr, numFont);
                g.DrawString(numStr, numFont, numBrush,
                    circleRect.X + (circleRect.Width - numSize.Width) / 2f + 0.5f,
                    circleRect.Y + (circleRect.Height - numSize.Height) / 2f);

                // Step text
                g.DrawString(steps[i], stepFont, stepBrush,
                    new RectangleF(48, y + 1, Width - 48 - 18, 22));

                y += 28;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var surfaceBrush = new SolidBrush(Surface);
            pevent.Graphics.FillRectangle(surfaceBrush, 0, 0, Width, Height);

            using var cardBrush = new SolidBrush(InfoBg);
            using var borderPen = new Pen(InfoBorder, 1f);
            using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            pevent.Graphics.FillPath(cardBrush, path);
            pevent.Graphics.DrawPath(borderPen, path);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ── Digit box with subtle focus border ──────────────────────────

    private sealed class DigitBox : TextBox
    {
        public DigitBox()
        {
            BorderStyle = BorderStyle.None;
        }

        protected override void OnPaint(PaintEventArgs e) => base.OnPaint(e);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                return cp;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Parent?.Invalidate(Bounds);
            SelectAll();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Parent?.Invalidate(Bounds);
        }
    }

    // ── Accent button (owner-drawn, hover/press/disabled states) ────

    private sealed class AccentButton : Button
    {
        private bool _hover;
        private bool _down;

        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            BackColor = AccentPrimary;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 11f, FontStyle.Bold);
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
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color bg;
            Color fg = Color.White;
            if (!Enabled)    { bg = Disabled; fg = Color.White; }
            else if (_down)  bg = AccentPressed;
            else if (_hover) bg = AccentHover;
            else             bg = AccentPrimary;

            using var brush = new SolidBrush(bg);
            using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 8);
            g.FillPath(brush, path);

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
