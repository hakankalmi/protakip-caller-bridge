using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// Test çağrısı giriş dialog'u. Tam ekran — DPI scaling ile küçücük kalan
/// önceki sürümü Hakan onayladı ölçüsünde büyük puntoya geçirdik.
/// Kullanıcı kayıtlı veya kayıtsız müşteri numarası girerek iki akışı da
/// test edebilir.
/// </summary>
public class TestCallDialog : Form
{
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);
    private static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);
    private static readonly Color InputBg       = Color.FromArgb(248, 250, 252);

    private readonly TextBox _phoneBox;
    private readonly AccentButton _sendBtn;

    public string PhoneNumber => (_phoneBox.Text ?? "").Trim();

    public TestCallDialog()
    {
        Text = "ProTakip Caller Id — Test çağrısı";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 11f);
        ShowInTaskbar = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        // Sağ üstte X
        var closeBtn = new Button
        {
            Text = "×",
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(56, 56),
            ForeColor = TextMuted,
            BackColor = Color.White,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (_, __) => Close();
        Controls.Add(closeBtn);
        closeBtn.Location = new Point(Screen.PrimaryScreen!.WorkingArea.Width - 72, 16);
        closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.White,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 720));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Controls.Add(root);
        root.SendToBack();

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(32, 120, 32, 32),
        };
        root.Controls.Add(stack, 1, 0);

        var title = new Label
        {
            Text = "Test Çağrısı",
            AutoSize = true,
            Font = new Font("Segoe UI", 36f, FontStyle.Bold),
            ForeColor = AccentPrimary,
            Margin = new Padding(0, 0, 0, 20),
        };
        stack.Controls.Add(title);

        var instr = new Label
        {
            Text = "Web panelde göstereceğimiz numarayı girin.\n\n"
                 + "Kayıtlı bir müşterinin numarasını girerseniz müşteri kartı açılır.\n"
                 + "Kayıtsız bir numara girerseniz yeni kayıt butonu belirir.",
            AutoSize = true,
            MaximumSize = new Size(656, 0),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 13f),
            Margin = new Padding(0, 0, 0, 48),
        };
        stack.Controls.Add(instr);

        var label = new Label
        {
            Text = "Telefon numarası",
            AutoSize = true,
            ForeColor = TextStrong,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12),
        };
        stack.Controls.Add(label);

        _phoneBox = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 32f, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Left,
            BackColor = InputBg,
            ForeColor = TextStrong,
            Size = new Size(656, 80),
            MaxLength = 20,
            Text = "05551112233",
            Margin = new Padding(0, 0, 0, 32),
        };
        _phoneBox.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar)) return;
            if (!char.IsDigit(e.KeyChar) && "+- ()".IndexOf(e.KeyChar) < 0)
                e.Handled = true;
        };
        stack.Controls.Add(_phoneBox);

        _sendBtn = new AccentButton
        {
            Text = "GÖNDER",
            Size = new Size(656, 72),
            Margin = new Padding(0),
        };
        _sendBtn.Click += (_, __) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        stack.Controls.Add(_sendBtn);

        AcceptButton = _sendBtn;
        Load += (_, __) =>
        {
            _phoneBox.Focus();
            _phoneBox.SelectAll();
        };
    }

    protected override void OnClosing(CancelEventArgs e) => base.OnClosing(e);

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
            Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg = _down ? AccentPressed : _hover ? AccentHover : AccentPrimary;
            g.Clear(bg);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }
    }
}
