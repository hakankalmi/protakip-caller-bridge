using System.ComponentModel;

namespace ProTakipCallerBridge;

/// <summary>
/// Bridge tray → "Test çağrısı gönder" seçilince açılan giriş dialog'u.
/// Kullanıcıdan telefon numarası alır, varsayılan olarak kayıtsız müşteri
/// test numarasını gösterir ama kullanıcı kendi kayıtlı müşterilerinden
/// birinin numarasını girerek "kayıtlı müşteri" modal akışını da test
/// edebilir.
/// </summary>
public class TestCallDialog : Form
{
    private static readonly Color AccentPrimary = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentHover   = Color.FromArgb(29, 78, 216);
    private static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
    private static readonly Color TextStrong    = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted     = Color.FromArgb(71, 85, 105);
    private static readonly Color InputBg       = Color.FromArgb(248, 250, 252);
    private static readonly Color BorderSoft    = Color.FromArgb(203, 213, 225);

    private readonly TextBox _phoneBox;
    private readonly AccentButton _sendBtn;

    public string PhoneNumber => (_phoneBox.Text ?? "").Trim();

    public TestCallDialog()
    {
        Text = "ProTakip Caller Id — Test çağrısı";
        ClientSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.White,
            Padding = new Padding(40, 32, 40, 24),
        };
        Controls.Add(stack);

        var title = new Label
        {
            Text = "Test Çağrısı",
            AutoSize = true,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = AccentPrimary,
            Margin = new Padding(0, 0, 0, 8),
        };
        stack.Controls.Add(title);

        var instr = new Label
        {
            Text = "Web panelde göstereceğimiz numarayı girin.\n"
                 + "Kayıtlı bir müşterinin numarasını girerseniz müşteri\n"
                 + "kartı açılır, kayıtsız bir numara girerseniz yeni kayıt\n"
                 + "butonu belirir.",
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(0, 0, 0, 20),
        };
        stack.Controls.Add(instr);

        var label = new Label
        {
            Text = "Telefon numarası",
            AutoSize = true,
            ForeColor = TextStrong,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };
        stack.Controls.Add(label);

        _phoneBox = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 18f, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Left,
            BackColor = InputBg,
            ForeColor = TextStrong,
            Size = new Size(440, 48),
            MaxLength = 20,
            Text = "05551112233",
            Margin = new Padding(0, 0, 0, 18),
        };
        _phoneBox.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar)) return;
            // Numeric, +, space, -, parentheses — enough for any sane phone number format.
            if (!char.IsDigit(e.KeyChar) && "+- ()".IndexOf(e.KeyChar) < 0)
                e.Handled = true;
        };
        stack.Controls.Add(_phoneBox);

        _sendBtn = new AccentButton
        {
            Text = "GÖNDER",
            Size = new Size(440, 48),
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
            Font = new Font("Segoe UI", 11f, FontStyle.Bold);
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
