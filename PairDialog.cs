using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ProTakipCallerBridge;

/// <summary>
/// First-run pair screen. Redesigned to look at home next to the rest of
/// the ProTakip UI (the old one was stock WinForms controls on a white
/// box — title clipped, fonts collided, zero hierarchy). Layout:
///
///   ┌──────────────────────────────────────────┐
///   │  [P]  ProTakip Caller Id                 │  ← blue gradient header
///   │       Köprüyü eşleştir                   │
///   ├──────────────────────────────────────────┤
///   │                                           │
///   │  app.protakip.com'da caller-id           │
///   │  açılınca 6 haneli kodu girin.           │
///   │                                           │
///   │        ┌──┬──┬──┬──┬──┬──┐               │
///   │        │  │  │  │  │  │  │   6 slot      │
///   │        └──┴──┴──┴──┴──┴──┘               │
///   │                                           │
///   │        [    Eşleştir     ]               │
///   │                                           │
///   │  Kodu nereden alacağım? →                │  ← link
///   │                                           │
///   └──────────────────────────────────────────┘
///
/// Owner-drawn header, custom-paint button, invisible-bordered textbox
/// inside a rounded panel for the "segmented" look without shipping a
/// third-party lib.
/// </summary>
public class PairDialog : Form
{
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);   // blue-600
    private static readonly Color AccentDark    = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);    // slate-900
    private static readonly Color TextMuted     = Color.FromArgb(100, 116, 139); // slate-500
    private static readonly Color TextLight     = Color.FromArgb(148, 163, 184); // slate-400
    private static readonly Color Success       = Color.FromArgb(22, 163, 74);   // green-600
    private static readonly Color Error         = Color.FromArgb(220, 38, 38);   // red-600
    private static readonly Color SurfaceSoft   = Color.FromArgb(248, 250, 252); // slate-50
    private static readonly Color BorderSoft    = Color.FromArgb(226, 232, 240); // slate-200

    private readonly ApiClient _api;
    private readonly BridgeConfig _cfg;

    private readonly CodeEntry _codeEntry;
    private readonly FlatButton _pairBtn;
    private readonly Label _statusLabel;
    private readonly LinkLabel _openWebLink;

    public PairDialog(ApiClient api, BridgeConfig cfg)
    {
        _api = api;
        _cfg = cfg;

        // ── Form chrome ─────────────────────────────────────────────
        Text = "ProTakip Caller Id";
        Size = new Size(560, 440);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);
        DoubleBuffered = true;
        // ShowInTaskbar keeps the dialog discoverable even if it opens behind
        // the browser — user can Alt+Tab or click the taskbar entry to raise
        // it. Critical on Windows 11 where freshly-launched exe's from
        // Defender's "Run anyway" flow often don't get foreground focus.
        ShowInTaskbar = true;

        // ── Header ──────────────────────────────────────────────────
        var header = new HeaderPanel { Dock = DockStyle.Top, Height = 96 };

        // ── Body ────────────────────────────────────────────────────
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(36, 24, 36, 24),
        };

        var subtitle = new Label
        {
            Text = "Sekreter bilgisayarınızı ProTakip hesabınıza bağlıyorsunuz.",
            AutoSize = false,
            Size = new Size(488, 20),
            Location = new Point(36, 114),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5f),
        };

        var instruction = new Label
        {
            Text = "app.protakip.com'da sağ üstteki Caller ID göstergesine tıklayın, USB Cihaz seçip çıkan 6 haneli kodu aşağıya girin.",
            AutoSize = false,
            Size = new Size(488, 40),
            Location = new Point(36, 134),
            ForeColor = TextStrong,
            Font = new Font("Segoe UI", 9.5f),
        };

        _codeEntry = new CodeEntry
        {
            Location = new Point(36, 192),
            Size = new Size(488, 58),
        };
        _codeEntry.CodeChanged += (_, __) => _pairBtn!.Enabled = _codeEntry.CodeText.Length == 6;
        _codeEntry.CodeReady += async (_, __) => await DoPairAsync();

        _pairBtn = new FlatButton
        {
            Text = "Eşleştir",
            Location = new Point(36, 268),
            Size = new Size(488, 44),
            Enabled = false,
        };
        _pairBtn.Click += async (_, __) => await DoPairAsync();

        _statusLabel = new Label
        {
            Location = new Point(36, 320),
            Size = new Size(488, 20),
            ForeColor = TextMuted,
            Text = "",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.5f),
        };

        _openWebLink = new LinkLabel
        {
            Text = "app.protakip.com'u aç",
            Location = new Point(36, 354),
            Size = new Size(488, 20),
            LinkColor = AccentPrimary,
            ActiveLinkColor = AccentDark,
            LinkBehavior = LinkBehavior.HoverUnderline,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.5f),
        };
        _openWebLink.LinkClicked += (_, __) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://app.protakip.com",
                    UseShellExecute = true,
                });
            }
            catch { /* no-op */ }
        };

        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(subtitle);
        Controls.Add(instruction);
        Controls.Add(_codeEntry);
        Controls.Add(_pairBtn);
        Controls.Add(_statusLabel);
        Controls.Add(_openWebLink);

        // Dock order — header first so the other children sit below it.
        header.SendToBack();
        body.SendToBack();

        AcceptButton = _pairBtn;

        // Focus the first digit as soon as the form is visible.
        Load += (_, __) => _codeEntry.FocusFirst();
    }

    // ── Pair action ─────────────────────────────────────────────────

    private async Task DoPairAsync()
    {
        var code = _codeEntry.CodeText;
        if (code.Length != 6) return;

        _pairBtn.Enabled = false;
        _codeEntry.Enabled = false;
        _statusLabel.ForeColor = TextMuted;
        _statusLabel.Text = "Sunucuya bağlanılıyor...";

        try
        {
            var res = await _api.ClaimAsync(code, deviceSerial: null);
            if (res == null)
            {
                _statusLabel.ForeColor = Error;
                _statusLabel.Text = "Kod geçersiz veya süresi dolmuş. Web panelde yeni kod alın.";
                _pairBtn.Enabled = true;
                _codeEntry.Enabled = true;
                _codeEntry.FocusFirst();
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
            _pairBtn.Enabled = true;
            _codeEntry.Enabled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e) => base.OnClosing(e);

    // ──────────────────────────────────────────────────────────────
    //  Nested controls — keep PairDialog self-contained.
    // ──────────────────────────────────────────────────────────────

    /// <summary>Gradient header with a rounded badge logo + two-line title.</summary>
    private sealed class HeaderPanel : Panel
    {
        public HeaderPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Gradient band
            using (var brush = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(59, 130, 246),    // blue-500
                Color.FromArgb(29, 78, 216),     // blue-700
                LinearGradientMode.Horizontal))
            {
                g.FillRectangle(brush, ClientRectangle);
            }

            // Logo badge — rounded square with a big white "P"
            var badgeRect = new Rectangle(32, 22, 52, 52);
            using (var path = RoundedRect(badgeRect, 12))
            using (var badgeBrush = new LinearGradientBrush(
                badgeRect,
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(220, 232, 255),
                LinearGradientMode.Vertical))
            {
                g.FillPath(badgeBrush, path);
            }
            using (var logoFont = new Font("Segoe UI", 26, FontStyle.Bold))
            using (var logoBrush = new SolidBrush(AccentDark))
            {
                var sz = g.MeasureString("P", logoFont);
                g.DrawString("P", logoFont,
                    logoBrush,
                    badgeRect.X + (badgeRect.Width - sz.Width) / 2 + 1,
                    badgeRect.Y + (badgeRect.Height - sz.Height) / 2);
            }

            // Title
            using (var titleFont = new Font("Segoe UI", 14.5f, FontStyle.Bold))
            using (var subFont = new Font("Segoe UI", 10f))
            using (var white = new SolidBrush(Color.White))
            using (var whiteDim = new SolidBrush(Color.FromArgb(200, Color.White)))
            {
                g.DrawString("ProTakip Caller Id", titleFont, white, 100, 28);
                g.DrawString("Köprüyü eşleştir", subFont, whiteDim, 101, 54);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// 6-digit segmented code entry. Looks like six tight boxes but under
    /// the hood is a single TextBox positioned inside a rounded frame — we
    /// draw the segment dividers manually in <see cref="OnPaint"/>. Paste
    /// works (Ctrl+V of "123456" fills all slots).
    /// </summary>
    private sealed class CodeEntry : Panel
    {
        public event EventHandler? CodeChanged;
        public event EventHandler? CodeReady;

        private readonly TextBox _inner;

        public string CodeText => _inner.Text.Trim();

        public CodeEntry()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Size = new Size(488, 58);

            _inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 26f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 6,
                BackColor = Color.White,
                ForeColor = AccentPrimary,
                CharacterCasing = CharacterCasing.Upper,
            };
            _inner.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                    e.Handled = true;
            };
            _inner.TextChanged += (_, __) =>
            {
                CodeChanged?.Invoke(this, EventArgs.Empty);
                if (_inner.Text.Trim().Length == 6)
                    CodeReady?.Invoke(this, EventArgs.Empty);
                Invalidate();
            };

            Controls.Add(_inner);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Centre the TextBox. The TextBox only needs enough room for the
            // 6 characters + tracking — let the segment dividers we paint
            // behind do the visual work.
            int innerW = 360;
            _inner.Size = new Size(innerW, 44);
            _inner.Location = new Point((Width - innerW) / 2, (Height - 44) / 2 + 3);
        }

        public void FocusFirst() => _inner.Focus();

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Outer rounded frame
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = PairDialog.RoundedRectStatic(frame, 10))
            using (var pen = new Pen(BorderSoft, 1.4f))
            using (var fill = new SolidBrush(SurfaceSoft))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            // Six segment dividers — thin gray verticals at 1/6 intervals.
            var innerLeft = _inner.Left;
            var innerRight = _inner.Right;
            var slotWidth = (innerRight - innerLeft) / 6.0f;
            using var divider = new Pen(Color.FromArgb(215, 221, 232), 1f);
            for (int i = 1; i < 6; i++)
            {
                float x = innerLeft + slotWidth * i;
                g.DrawLine(divider, x, 12, x, Height - 12);
            }

            // Placeholder underscores where no digit typed yet
            if (string.IsNullOrEmpty(_inner.Text) && !_inner.Focused)
            {
                using var phBrush = new SolidBrush(TextLight);
                using var phFont = new Font("Consolas", 26f, FontStyle.Bold);
                for (int i = 0; i < 6; i++)
                {
                    float x = innerLeft + slotWidth * i + slotWidth / 2 - 8;
                    g.DrawString("·", phFont, phBrush, x, 10);
                }
            }
        }

        public new bool Enabled
        {
            get => base.Enabled;
            set
            {
                base.Enabled = value;
                _inner.Enabled = value;
                Invalidate();
            }
        }
    }

    /// <summary>
    /// Owner-drawn rounded button matching the web panel's blue CTA.
    /// WinForms' default button rendering looks like 2003 Office, so we
    /// paint it ourselves — flat, rounded, gradient, hover/press states.
    /// </summary>
    private sealed class FlatButton : Button
    {
        private bool _hover;
        private bool _down;

        public FlatButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = AccentPrimary;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            DoubleBuffered = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color top, bottom;
            if (!Enabled)
            {
                top = Color.FromArgb(203, 213, 225);    // slate-300
                bottom = Color.FromArgb(148, 163, 184); // slate-400
            }
            else if (_down)
            {
                top = AccentDark;
                bottom = Color.FromArgb(30, 64, 175);   // blue-800
            }
            else if (_hover)
            {
                top = AccentPrimary;
                bottom = AccentDark;
            }
            else
            {
                top = Color.FromArgb(59, 130, 246);     // blue-500
                bottom = AccentPrimary;
            }

            using var path = PairDialog.RoundedRectStatic(rect, 10);
            using var fill = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
            g.FillPath(fill, path);

            var text = Text ?? "";
            using var textBrush = new SolidBrush(Color.White);
            var sz = g.MeasureString(text, Font);
            g.DrawString(text, Font, textBrush,
                (Width - sz.Width) / 2,
                (Height - sz.Height) / 2);
        }
    }

    // Shared helper — used from nested controls via PairDialog.RoundedRectStatic.
    internal static GraphicsPath RoundedRectStatic(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
