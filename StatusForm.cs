using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProTakipCallerBridge;

/// <summary>
/// Main status window — opens when the user double-clicks the tray icon.
///
/// Design goals:
///   - "Everything at a glance" dashboard for the secretary. No scrolling
///     to find whether USB is alive or NetGSM is logged in.
///   - Premium, calm palette. Inspired by the web panel (app.protakip.com):
///     slate background, blue accents, emerald for OK, amber for pending,
///     rose for errors.
///   - Live updates — 1s timer refreshes timestamps so "son sinyal 3s önce"
///     actually counts up without the user needing to reopen the window.
///   - Single responsibility: shows state. Actions (pair again, test call,
///     logs) are still in the tray menu; repeating them here would clutter.
///
/// Layout:
///
///   ┌─ ProTakip Caller Id ────────────────────────────┐
///   │                                                  │
///   │  Sivas Halı Yıkama                              │
///   │  app.protakip.com ile bağlantı kuruldu          │
///   │                                                  │
///   │  ┌─ USB Caller ID ─┐  ┌─ NetGSM Bulut Santral ─┐│
///   │  │ ● Bağlı          │  │ ● Bağlı                 ││
///   │  │ Cihaz CID-8341   │  │ Hesap 8502772233        ││
///   │  │ 3 sn önce sinyal │  │ 12 sn önce arama        ││
///   │  └──────────────────┘  └─────────────────────────┘│
///   │                                                   │
///   │  ┌─ Son Çağrılar ────────────────────────────┐   │
///   │  │ 14:32  0532 123 4567   USB     ✓ iletildi │   │
///   │  │ 14:29  0555 987 6543   NetGSM  ✓ iletildi │   │
///   │  │ 14:15  0546 111 2222   USB     ✓ iletildi │   │
///   │  └──────────────────────────────────────────┘   │
///   │                                                   │
///   │  [Test çağrısı]  [Yeniden eşleştir]  [Log]       │
///   └──────────────────────────────────────────────────┘
/// </summary>
public class StatusForm : Form
{
    // Web paneliyle birebir palet — kullanıcı iki yüzey arasında gidip
    // geldiğinde renk değişikliğinden rahatsız olmasın.
    private static readonly Color Surface       = Color.FromArgb(248, 250, 252); // slate-50
    private static readonly Color CardBg        = Color.White;
    private static readonly Color CardBorder    = Color.FromArgb(226, 232, 240); // slate-200
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);    // slate-900
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);   // slate-600
    private static readonly Color TextFaint     = Color.FromArgb(148, 163, 184); // slate-400
    private static readonly Color Accent        = Color.FromArgb(37, 99, 235);   // blue-600
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);   // blue-700
    private static readonly Color Success       = Color.FromArgb(22, 163, 74);   // green-600
    private static readonly Color SuccessSoft   = Color.FromArgb(220, 252, 231); // green-100
    private static readonly Color Warning       = Color.FromArgb(217, 119, 6);   // amber-600
    private static readonly Color WarningSoft   = Color.FromArgb(254, 243, 199); // amber-100
    private static readonly Color Danger        = Color.FromArgb(220, 38, 38);   // red-600
    private static readonly Color DangerSoft    = Color.FromArgb(254, 226, 226); // red-100
    private static readonly Color Divider       = Color.FromArgb(241, 245, 249); // slate-100

    private readonly BridgeConfig _cfg;

    private readonly Label _companyLabel;
    private readonly Label _subtitleLabel;
    private readonly StatusCard _usbCard;
    private readonly StatusCard _netgsmCard;
    private readonly CallListPanel _callList;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    /// <summary>
    /// Action button handlers supplied by <see cref="Program"/> — this form
    /// deliberately doesn't know how to pair, test, or open logs.
    /// </summary>
    public event EventHandler? TestCallRequested;
    public event EventHandler? RepairRequested;
    public event EventHandler? OpenLogRequested;

    public StatusForm(BridgeConfig cfg)
    {
        _cfg = cfg;

        Text = "ProTakip Caller Id";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        BackColor = Surface;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(780, 600);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

        // Close box should just hide the window — bridge stays alive in the
        // tray. Matches OneDrive / Teams behaviour for always-running apps.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // ── Header ────────────────────────────────────────────────────
        _companyLabel = new Label
        {
            Text = _cfg.CompanyName ?? "ProTakip Caller Id",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = TextStrong,
            AutoSize = true,
            Location = new Point(32, 28),
            BackColor = Color.Transparent,
        };
        Controls.Add(_companyLabel);

        _subtitleLabel = new Label
        {
            Text = "Sekreter PC köprüsü — app.protakip.com ile bağlı",
            Font = new Font("Segoe UI", 10f),
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(32, 70),
            BackColor = Color.Transparent,
        };
        Controls.Add(_subtitleLabel);

        // ── Status cards (2x1 grid) ───────────────────────────────────
        int cardY = 110;
        int cardW = 355;
        int cardH = 130;
        int cardGap = 16;
        int cardLeft = 32;

        _usbCard = new StatusCard
        {
            Location = new Point(cardLeft, cardY),
            Size = new Size(cardW, cardH),
            Title = "USB Caller ID",
        };
        _usbCard.SetState(StatusKind.Pending, "Bağlantı bekleniyor",
            "USB Caller ID cihazını takın veya cihaz sürücüsünü kontrol edin.");
        Controls.Add(_usbCard);

        _netgsmCard = new StatusCard
        {
            Location = new Point(cardLeft + cardW + cardGap, cardY),
            Size = new Size(cardW, cardH),
            Title = "NetGSM Bulut Santral",
        };
        _netgsmCard.SetState(StatusKind.Disabled, "Yapılandırılmadı",
            "Web panelinden Caller ID → Sanal Santral → NetGSM bilgilerini girin.");
        Controls.Add(_netgsmCard);

        // ── Recent calls ──────────────────────────────────────────────
        int listY = cardY + cardH + 18;
        var listHeader = new Label
        {
            Text = "SON ÇAĞRILAR",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = TextFaint,
            AutoSize = true,
            Location = new Point(cardLeft, listY),
            BackColor = Color.Transparent,
        };
        Controls.Add(listHeader);

        _callList = new CallListPanel
        {
            Location = new Point(cardLeft, listY + 22),
            Size = new Size(cardW * 2 + cardGap, 260),
        };
        Controls.Add(_callList);

        // ── Action bar ────────────────────────────────────────────────
        int actionY = ClientSize.Height - 52;
        var testBtn = new LinkActionButton
        {
            Text = "Test çağrısı",
            Location = new Point(cardLeft, actionY),
            Size = new Size(140, 32),
        };
        testBtn.Click += (_, _) => TestCallRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(testBtn);

        var repairBtn = new LinkActionButton
        {
            Text = "Yeniden eşleştir",
            Location = new Point(testBtn.Right + 8, actionY),
            Size = new Size(160, 32),
        };
        repairBtn.Click += (_, _) => RepairRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(repairBtn);

        var logBtn = new LinkActionButton
        {
            Text = "Log dosyası",
            Location = new Point(repairBtn.Right + 8, actionY),
            Size = new Size(130, 32),
        };
        logBtn.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(logBtn);

        // ── Refresh timer ─────────────────────────────────────────────
        // 1 saniye — "3 sn önce" → "4 sn önce" gibi akıcı sayım. Formun
        // gizli olduğu durumlarda da çalışmasın diye Visible kontrolü
        // handler içinde.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => { if (Visible) RefreshTimestamps(); };
        _refreshTimer.Start();
    }

    // ── Public state updates (called from Program.cs) ─────────────────

    public void UpdateCompany(string? companyName)
    {
        _companyLabel.Text = string.IsNullOrWhiteSpace(companyName)
            ? "ProTakip Caller Id"
            : companyName;
    }

    public void UpdateUsb(bool connected, string? deviceSerial, DateTime? lastSignalAt)
    {
        if (!connected)
        {
            _usbCard.SetState(StatusKind.Pending, "Cihaz bekleniyor",
                "USB Caller ID cihazını takın veya cihaz sürücüsünü kontrol edin.");
            return;
        }
        var serial = string.IsNullOrWhiteSpace(deviceSerial) ? "—" : deviceSerial!;
        var lastMsg = lastSignalAt.HasValue
            ? $"Son sinyal {HumanSince(lastSignalAt.Value)}"
            : "Sinyal bekleniyor";
        _usbCard.SetState(StatusKind.Ok, "Bağlı",
            $"Cihaz {serial}\n{lastMsg}");
        _usbCard.LastTimestamp = lastSignalAt;
        _usbCard.LiveLabelBuilder = ts => $"Son sinyal {HumanSince(ts)}";
        _usbCard.LiveLabelPrefixLines = 1; // "Cihaz ..." ilk satırda kalsın
    }

    public void UpdateNetgsm(NetgsmState state, string? username = null, DateTime? lastEventAt = null, string? error = null)
    {
        switch (state)
        {
            case NetgsmState.Disabled:
                _netgsmCard.SetState(StatusKind.Disabled, "Yapılandırılmadı",
                    "Web panelinden Caller ID → Sanal Santral → NetGSM bilgilerini girin.");
                _netgsmCard.LastTimestamp = null;
                break;

            case NetgsmState.Connecting:
                _netgsmCard.SetState(StatusKind.Pending, "Bağlanıyor…",
                    $"Hesap {username ?? "-"}\ncrmsntrl.netgsm.com.tr:9110");
                _netgsmCard.LastTimestamp = null;
                break;

            case NetgsmState.Connected:
                var lastLine = lastEventAt.HasValue
                    ? $"Son olay {HumanSince(lastEventAt.Value)}"
                    : "Olay bekleniyor";
                _netgsmCard.SetState(StatusKind.Ok, "Bağlı",
                    $"Hesap {username ?? "-"}\n{lastLine}");
                _netgsmCard.LastTimestamp = lastEventAt;
                _netgsmCard.LiveLabelBuilder = ts => $"Son olay {HumanSince(ts)}";
                _netgsmCard.LiveLabelPrefixLines = 1;
                break;

            case NetgsmState.Error:
                _netgsmCard.SetState(StatusKind.Error, "Hata",
                    $"Hesap {username ?? "-"}\n{error ?? "Bağlantı kesildi, yeniden deneniyor"}");
                _netgsmCard.LastTimestamp = null;
                break;
        }
    }

    public void AddRecentCall(string phoneNumber, string source, bool success)
    {
        _callList.AddCall(new RecentCall
        {
            PhoneNumber = phoneNumber,
            Source = source,
            Success = success,
            At = DateTime.Now,
        });
    }

    private void RefreshTimestamps()
    {
        _usbCard.RefreshLiveLabel();
        _netgsmCard.RefreshLiveLabel();
        _callList.Invalidate();
    }

    // "3 sn önce", "12 dk önce", "2 sa önce" formatı. Çok kısa süreler
    // için "az önce" sekreter kafa karıştırmasın diye.
    internal static string HumanSince(DateTime stamp)
    {
        var diff = DateTime.UtcNow - stamp.ToUniversalTime();
        if (diff.TotalSeconds < 3) return "az önce";
        if (diff.TotalSeconds < 60) return $"{(int)diff.TotalSeconds} sn önce";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} dk önce";
        if (diff.TotalHours < 24)   return $"{(int)diff.TotalHours} sa önce";
        return $"{(int)diff.TotalDays} gün önce";
    }

    // ── Card sub-control ──────────────────────────────────────────────

    private sealed class StatusCard : Panel
    {
        private readonly Label _title;
        private readonly Panel _dot;
        private readonly Label _statusLabel;
        private readonly Label _bodyLabel;

        public string Title
        {
            get => _title.Text;
            set => _title.Text = value;
        }

        public string StatusText
        {
            get => _statusLabel.Text;
            set => _statusLabel.Text = value;
        }

        public StatusKind StatusKind { get; private set; } = StatusKind.Pending;

        /// <summary>Timestamp the "live" bottom line should count up from, if any.</summary>
        public DateTime? LastTimestamp { get; set; }

        /// <summary>Builds the refreshed bottom line from <see cref="LastTimestamp"/>.</summary>
        public Func<DateTime, string>? LiveLabelBuilder { get; set; }

        /// <summary>How many lines at the top of the body label stay fixed (e.g. "Cihaz X\n...").</summary>
        public int LiveLabelPrefixLines { get; set; }

        public StatusCard()
        {
            BackColor = CardBg;
            Padding = new Padding(18, 16, 18, 16);
            DoubleBuffered = true;

            _title = new Label
            {
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = TextFaint,
                AutoSize = true,
                Location = new Point(18, 14),
                BackColor = Color.Transparent,
            };
            Controls.Add(_title);

            _dot = new Panel
            {
                Size = new Size(10, 10),
                Location = new Point(18, 46),
                BackColor = TextFaint,
            };
            _dot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(_dot.BackColor);
                e.Graphics.FillEllipse(brush, 0, 0, _dot.Width - 1, _dot.Height - 1);
            };
            _dot.BackColor = CardBg;
            Controls.Add(_dot);

            _statusLabel = new Label
            {
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TextStrong,
                AutoSize = true,
                Location = new Point(34, 40),
                BackColor = Color.Transparent,
            };
            Controls.Add(_statusLabel);

            _bodyLabel = new Label
            {
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = TextMuted,
                AutoSize = false,
                Location = new Point(18, 72),
                Size = new Size(320, 50),
                BackColor = Color.Transparent,
            };
            Controls.Add(_bodyLabel);
        }

        public void SetState(StatusKind kind, string status, string body)
        {
            StatusKind = kind;
            _statusLabel.Text = status;
            _bodyLabel.Text = body;

            Color dotColor = kind switch
            {
                StatusKind.Ok       => Success,
                StatusKind.Pending  => Warning,
                StatusKind.Error    => Danger,
                StatusKind.Disabled => TextFaint,
                _                   => TextFaint,
            };
            Color statusColor = kind switch
            {
                StatusKind.Ok       => TextStrong,
                StatusKind.Pending  => TextStrong,
                StatusKind.Error    => Danger,
                StatusKind.Disabled => TextMuted,
                _                   => TextMuted,
            };
            _dot.BackColor = dotColor;
            _dot.Invalidate();
            _statusLabel.ForeColor = statusColor;
            Invalidate();
        }

        /// <summary>
        /// Update the last line of the body without touching the rest —
        /// called every second by the refresh timer so "12 sn önce" keeps
        /// ticking without re-flooring the card.
        /// </summary>
        public void RefreshLiveLabel()
        {
            if (LiveLabelBuilder == null || !LastTimestamp.HasValue) return;

            var lines = _bodyLabel.Text.Split('\n');
            if (lines.Length == 0) return;

            var idx = Math.Min(LiveLabelPrefixLines, lines.Length - 1);
            var newLast = LiveLabelBuilder(LastTimestamp.Value);
            if (lines[idx] == newLast) return;

            lines[idx] = newLast;
            _bodyLabel.Text = string.Join("\n", lines);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Border + subtle shadow outline — single-pixel stroke, no
            // drop shadow. Looks consistent across high-DPI monitors where
            // soft shadows tend to render fuzzy.
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(CardBorder, 1f);
            using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // Rounded background — filled first so the stroke in OnPaint
            // rides on top.
            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(BackColor);
            using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 10);
            // Fill the surface behind the rounded rect too so corners blend.
            using var surfaceBrush = new SolidBrush(Surface);
            pevent.Graphics.FillRectangle(surfaceBrush, 0, 0, Width, Height);
            pevent.Graphics.FillPath(brush, path);
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

    // ── Recent calls list ─────────────────────────────────────────────

    private sealed class RecentCall
    {
        public DateTime At { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public string Source { get; set; } = "usb";
        public bool Success { get; set; }
    }

    private sealed class CallListPanel : Panel
    {
        private const int MaxCalls = 40;
        private readonly LinkedList<RecentCall> _calls = new();

        public CallListPanel()
        {
            BackColor = CardBg;
            DoubleBuffered = true;
            AutoScroll = true;
        }

        public void AddCall(RecentCall call)
        {
            _calls.AddFirst(call);
            while (_calls.Count > MaxCalls) _calls.RemoveLast();
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var surfaceBrush = new SolidBrush(Surface);
            pevent.Graphics.FillRectangle(surfaceBrush, 0, 0, Width, Height);
            using var brush = new SolidBrush(CardBg);
            using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 10);
            pevent.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Card border
            using (var pen = new Pen(CardBorder, 1f))
            using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10))
                g.DrawPath(pen, path);

            if (_calls.Count == 0)
            {
                using var emptyFont = new Font("Segoe UI", 10f);
                using var emptyBrush = new SolidBrush(TextFaint);
                var msg = "Henüz çağrı yok — gelen bir arama olduğunda burada listelenir.";
                var sz = g.MeasureString(msg, emptyFont);
                g.DrawString(msg, emptyFont, emptyBrush,
                    (Width - sz.Width) / 2f, (Height - sz.Height) / 2f);
                return;
            }

            int rowH = 44;
            int pad = 18;
            int y = 8;

            using var timeFont = new Font("Segoe UI", 9f);
            using var timeBrush = new SolidBrush(TextFaint);
            using var phoneFont = new Font("Consolas", 11f, FontStyle.Bold);
            using var phoneBrush = new SolidBrush(TextStrong);
            using var sourceFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var statusFont = new Font("Segoe UI", 9f);
            using var dividerPen = new Pen(Divider, 1f);

            bool first = true;
            foreach (var call in _calls)
            {
                if (y + rowH > Height - 4) break;

                if (!first)
                {
                    g.DrawLine(dividerPen, pad, y, Width - pad, y);
                }
                first = false;

                var midY = y + (rowH - 14) / 2f;

                g.DrawString(call.At.ToString("HH:mm"),
                    timeFont, timeBrush, pad, midY);

                g.DrawString(FormatPhoneNumber(call.PhoneNumber),
                    phoneFont, phoneBrush, pad + 60, midY - 2);

                // Source pill — "USB" or "NetGSM" with subtle background.
                var sourceText = call.Source?.ToUpperInvariant() == "NETGSM" ? "NetGSM" : "USB";
                Color pillBg, pillFg;
                if (sourceText == "NetGSM")
                {
                    pillBg = Color.FromArgb(224, 231, 255);  // indigo-100
                    pillFg = Color.FromArgb(67, 56, 202);    // indigo-700
                }
                else
                {
                    pillBg = Color.FromArgb(219, 234, 254);  // blue-100
                    pillFg = Color.FromArgb(29, 78, 216);    // blue-700
                }
                var pillX = pad + 220;
                var pillSize = g.MeasureString(sourceText, sourceFont);
                var pillRect = new RectangleF(pillX, midY - 2, pillSize.Width + 16, 20);
                using (var pillBrush = new SolidBrush(pillBg))
                using (var pillPath = RoundedRectF(pillRect, 8))
                    g.FillPath(pillBrush, pillPath);
                using (var pillForeBrush = new SolidBrush(pillFg))
                    g.DrawString(sourceText, sourceFont, pillForeBrush,
                        pillRect.X + 8, pillRect.Y + 3);

                // Status on the right.
                var statusText = call.Success ? "✓ iletildi" : "✗ hata";
                using var statusBrush = new SolidBrush(call.Success ? Success : Danger);
                var statusSize = g.MeasureString(statusText, statusFont);
                g.DrawString(statusText, statusFont, statusBrush,
                    Width - pad - statusSize.Width, midY);

                y += rowH;
            }
        }

        private static string FormatPhoneNumber(string raw)
        {
            // 05xx xxx xx xx style — nothing fancy, just readable spacing.
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length == 11 && digits.StartsWith("0"))
                return $"{digits[..4]} {digits[4..7]} {digits[7..9]} {digits[9..]}";
            if (digits.Length == 10)
                return $"0{digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..]}";
            return raw;
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

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectF(RectangleF r, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ── Flat action button (link-style) ───────────────────────────────

    private sealed class LinkActionButton : Button
    {
        public LinkActionButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = CardBorder;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249); // slate-100
            BackColor = CardBg;
            ForeColor = TextStrong;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            UseCompatibleTextRendering = false;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);
            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }
    }
}

public enum StatusKind
{
    Ok,
    Pending,
    Error,
    Disabled,
}

public enum NetgsmState
{
    Disabled,
    Connecting,
    Connected,
    Error,
}
