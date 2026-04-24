using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ProTakipCallerBridgeCom
{
    /// <summary>
    /// Minimal ProTakip Caller ID köprüsü — CID v6 cihazı için cidv5callerid
    /// ActiveX COM kontrolünü kullanır (NegroPos pattern). cidshow/cid.dll
    /// SetEvents API'si CID v6 için CallerID event'i fire etmiyordu, sadece
    /// COM arayüzü çalışıyor.
    ///
    /// Akış:
    ///   1. Form açılır, `AxCIDv5` ActiveX control'u invisible olarak eklenir.
    ///   2. ActiveX OnCallerID event'i bir çağrı için fire ettiğinde,
    ///      e.phoneNumber değeri alınır, normalize edilir ve backend'in
    ///      /caller-id/ingest endpoint'ine Bearer token ile POST edilir.
    ///   3. Form kullanıcıya durum gösterir (Bağlantı, son arama, son hata).
    ///
    /// Config:
    ///   %APPDATA%\ProTakipCallerBridgeCom\config.ini  (pair token)
    ///   appsettings içinde gerekiyorsa API URL'si.
    ///
    /// Not: bu ilk teşhis sürümü — pair flow sonraki commit'te eklenecek.
    /// Bu sürüm sadece "arama yakalandı mı" sorusunun cevabını arıyor.
    /// Token geçici olarak BridgeConfig'den (aynı klasördeki config.ini)
    /// okunacak; yoksa kullanıcıya yapıştırması için manuel alan.
    /// </summary>
    public class MainForm : Form
    {
        private readonly Axcidv5callerid.AxCIDv5 _cid;
        private readonly ListBox _logList;
        private readonly Label _statusLabel;
        private readonly Label _deviceLabel;
        private readonly TextBox _tokenBox;
        private readonly Button _saveTokenBtn;
        private NotifyIcon _tray;
        private bool _reallyExit;
        private string _apiBase = "https://api.protakip.com/api";
        private string _deviceToken = string.Empty;

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProTakipCallerBridgeCom");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.ini");
        private static readonly string LogPath = Path.Combine(ConfigDir, "bridge.log");

        public MainForm()
        {
            Program.LogLine("MainForm ctor: başladı");
            CheckForIllegalCrossThreadCalls = false;

            Directory.CreateDirectory(ConfigDir);
            LoadConfig();
            Program.LogLine("MainForm ctor: config yüklendi (token len=" + _deviceToken.Length + ")");

            Text = "ProTakip Caller Id — COM";
            ClientSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Program.LogLine("MainForm ctor: form özellikleri set edildi");

            // ActiveX control oluşturma en riskli kısım — cidv5callerid.dll
            // sistemde regsvr32 ile kayıtlı değilse burada COMException
            // fırlar. Hata logu ile kullanıcıya ne olduğunu göstermek için
            // sarmaladık.
            try
            {
                Program.LogLine("ActiveX control instantiating: Axcidv5callerid.AxCIDv5");
                _cid = new Axcidv5callerid.AxCIDv5
                {
                    Visible = false,
                    Location = new Point(0, 0),
                    Size = new Size(10, 10),
                };
                Program.LogLine("ActiveX control instantiated OK");

                ((ISupportInitialize)_cid).BeginInit();
                Program.LogLine("ActiveX BeginInit OK");

                Controls.Add(_cid);
                Program.LogLine("ActiveX Controls.Add OK");

                ((ISupportInitialize)_cid).EndInit();
                Program.LogLine("ActiveX EndInit OK — COM object fully initialized");

                _cid.OnCallerID += Cid_OnCallerID;
                Program.LogLine("ActiveX OnCallerID event handler subscribed");

                // NegroPos pattern (anaForm.cs:607-608) — Hide+Start yapmadan
                // ActiveX cihazla iletişime geçmiyor, Command() boş dönüyor,
                // OnCallerID event'i asla fire etmiyor. Start() kritik.
                _cid.Hide();
                Program.LogLine("ActiveX Hide() OK");
                _cid.Start();
                Program.LogLine("ActiveX Start() OK — cihaz dinleme başladı");
            }
            catch (Exception ex)
            {
                Program.LogLine("ActiveX init FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Program.LogLine(ex.ToString());
                MessageBox.Show(
                    "Caller ID COM bileşeni yüklenemedi:\n\n" + ex.Message +
                    "\n\nMuhtemelen cidv5callerid.dll sistemde kayıtlı değil. NegroPos kurulu mu?",
                    "ProTakip Caller Id — COM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw; // kritik hatayı Program.Main yakalayıp görsel gösterecek
            }

            _statusLabel = new Label
            {
                Text = "Başlatılıyor...",
                Location = new Point(12, 12),
                Size = new Size(616, 24),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            };
            Controls.Add(_statusLabel);

            _deviceLabel = new Label
            {
                Text = "Cihaz: —",
                Location = new Point(12, 40),
                Size = new Size(616, 20),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.DimGray,
            };
            Controls.Add(_deviceLabel);

            var tokenTitle = new Label
            {
                Text = "Eşleşme kodu / Token (pair sonrası yapıştırın):",
                Location = new Point(12, 70),
                Size = new Size(400, 20),
            };
            Controls.Add(tokenTitle);

            // PlaceholderText .NET 5+ — net40'ta yok.
            _tokenBox = new TextBox
            {
                Location = new Point(12, 92),
                Size = new Size(520, 23),
                Text = _deviceToken,
            };
            Controls.Add(_tokenBox);

            _saveTokenBtn = new Button
            {
                Text = "Kaydet",
                Location = new Point(540, 91),
                Size = new Size(88, 25),
            };
            _saveTokenBtn.Click += (_, __) => OnSaveClicked();
            Controls.Add(_saveTokenBtn);

            _logList = new ListBox
            {
                Location = new Point(12, 130),
                Size = new Size(616, 320),
                IntegralHeight = false,
                Font = new Font("Consolas", 9f),
            };
            Controls.Add(_logList);

            AppendLog("=== Bridge COM başladı — TargetFramework=net40");
            AppendLog("Config: " + ConfigPath);
            AppendLog("Log: " + LogPath);

            UpdateStatusLabel();

            // ActiveX'ten cihaz bilgisi alıp her saniye status güncelle.
            var tick = new Timer { Interval = 1000 };
            tick.Tick += (_, __) => RefreshDeviceStatus();
            tick.Start();

            // Heartbeat — 60 saniyede bir /caller-id/ping. Web panelin
            // "Caller ID: Bağlı" göstermesi buna bağlı. İlk ping'i 3 sn sonra
            // at ki token kaydedildikten sonra hemen bağlantı durumu gözüksün.
            _pingTimer = new Timer { Interval = 3000 };
            _pingTimer.Tick += (_, __) =>
            {
                _pingTimer.Interval = 60000; // ilk ping sonrası 60 s
                if (!string.IsNullOrEmpty(_deviceToken)) SendPing();
            };
            _pingTimer.Start();

            // Tray icon — form kapatılınca (X) process ölmez, tray'e gizlenir.
            // Arka planda ActiveX dinlemeye devam eder. Double-click geri açar.
            InitTray();

            // Minimize veya close → tray'e gizle (gerçek çıkış için tray menüsü).
            Resize += (_, __) =>
            {
                if (WindowState == FormWindowState.Minimized) HideToTray(showBalloon: true);
            };
            FormClosing += (_, e) =>
            {
                if (_reallyExit) return;
                e.Cancel = true;
                HideToTray(showBalloon: true);
            };
        }

        // Tray icon renk durumları — bridge genel sağlığına göre değişir.
        private enum TrayState { Pending, Ok, Error }

        private void InitTray()
        {
            // Tray ikonunu runtime'da çiz: yeşil/amber/kırmızı daire üstünde
            // beyaz telefon glyph. Küçük bir asset dosyası paketlemekten
            // kaçınıyoruz, Windows 16/20/24 px'e downscale ediyor.
            var trayIcon = BuildTrayIcon(TrayState.Pending);
            Icon = trayIcon;  // form title bar + taskbar ikonu da aynı olsun

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Pencereyi Aç");
            openItem.Click += (_, __) => ShowFromTray();
            var exitItem = new ToolStripMenuItem("Çıkış");
            exitItem.Click += (_, __) =>
            {
                _reallyExit = true;
                _tray.Visible = false;
                Application.Exit();
            };
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon
            {
                Icon = trayIcon,
                Text = "ProTakip Caller Id",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, __) => ShowFromTray();
            Program.LogLine("Tray icon created");
        }

        private void HideToTray(bool showBalloon)
        {
            Hide();
            ShowInTaskbar = false;
            if (showBalloon && _tray != null)
            {
                _tray.ShowBalloonTip(
                    3000,
                    "ProTakip Caller Id çalışıyor",
                    "Bridge tray'de arka planda dinliyor. Pencereyi tekrar açmak için simgeye çift tıklayın.",
                    ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
            Activate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            base.Dispose(disposing);
        }

        private void UpdateTrayIcon()
        {
            if (_tray == null) return;
            TrayState s;
            if (string.IsNullOrEmpty(_deviceToken)) s = TrayState.Pending;
            else if (_isConnected) s = TrayState.Ok;
            else s = TrayState.Error;

            var old = _tray.Icon;
            _tray.Icon = BuildTrayIcon(s);
            try { old?.Dispose(); } catch { }
        }

        /// <summary>
        /// Tray ikonunu runtime'da çizer. 32x32 kaynak bitmap — Windows 16/20
        /// /24 piksele downscale eder. Renkli daire + beyaz telefon glyph,
        /// küçük boyutta bile okunaklı.
        /// </summary>
        private static Icon BuildTrayIcon(TrayState state)
        {
            Color fill;
            switch (state)
            {
                case TrayState.Ok:      fill = Color.FromArgb(22, 163, 74);  break; // green-600
                case TrayState.Error:   fill = Color.FromArgb(220, 38, 38);  break; // red-600
                default:                fill = Color.FromArgb(217, 119, 6);  break; // amber-600
            }

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(fill))
                        g.FillEllipse(brush, 2, 2, 28, 28);

                    using (var pen = new Pen(Color.White, 2.4f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        // Stilize edilmiş ☏ — 135°'den 270° yay.
                        g.DrawArc(pen, 9, 9, 14, 14, 135, 270);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
                finally { DestroyIcon(hIcon); }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly Timer _pingTimer;

        private void UpdateStatusLabel()
        {
            if (string.IsNullOrEmpty(_deviceToken))
                _statusLabel.Text = "Token gerekli — yapıştırıp Kaydet'e basın";
            else
                _statusLabel.Text = "Dinleniyor (" + (_isConnected ? "Bağlı" : "bağlantı kontrol ediliyor…") + ")";
        }

        private bool _isConnected;

        private void OnSaveClicked()
        {
            var raw = _tokenBox.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                AppendLog("Boş alan — token veya eşleşme kodu yapıştırın");
                return;
            }

            // 4-10 haneli sayısal dizi → pair code; onu /caller-id/claim'e
            // POST edip dönen gerçek token'ı sakla. Aksi halde doğrudan token
            // kabul et.
            if (IsLikelyPairCode(raw))
            {
                AppendLog("Eşleşme kodu algılandı (" + raw + ") — /caller-id/claim çağrılıyor...");
                _saveTokenBtn.Enabled = false;
                _saveTokenBtn.Text = "Eşleşiliyor...";
                try
                {
                    var serial = SafeGetSerial();
                    var resp = PostClaim(raw, serial);
                    if (resp == null)
                    {
                        AppendLog("  ✗ Eşleşme başarısız — kod yanlış veya süresi dolmuş olabilir");
                    }
                    else
                    {
                        _deviceToken = resp.DeviceToken ?? string.Empty;
                        _tokenBox.Text = _deviceToken;
                        SaveConfig();
                        AppendLog("  ✓ Eşleşme başarılı — firma: " + (resp.CompanyName ?? "?") +
                                  ", deviceId: " + resp.DeviceId);
                        SendPing(); // hemen heartbeat at ki web panel "Bağlı" olsun
                    }
                }
                finally
                {
                    _saveTokenBtn.Enabled = true;
                    _saveTokenBtn.Text = "Kaydet";
                }
            }
            else
            {
                _deviceToken = raw;
                SaveConfig();
                AppendLog("Token kaydedildi (" + _deviceToken.Length + " karakter)");
                SendPing();
            }

            UpdateStatusLabel();
            UpdateTrayIcon();
        }

        private static bool IsLikelyPairCode(string s)
        {
            if (s.Length < 4 || s.Length > 10) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        private string SafeGetSerial()
        {
            try { return _cid?.Command("Serial") ?? string.Empty; }
            catch { return string.Empty; }
        }

        private ClaimResponse PostClaim(string pairCode, string deviceSerial)
        {
            try
            {
                var url = _apiBase.TrimEnd('/') + "/caller-id/claim";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 15000;

                var body =
                    "{\"pairCode\":\"" + JsonEscape(pairCode) + "\"," +
                    "\"deviceSerial\":\"" + JsonEscape(deviceSerial) + "\"," +
                    "\"deviceInfo\":\"" + JsonEscape(Environment.MachineName + " · win · bridge-com 1.0") + "\"}";
                var bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                    s.Write(bytes, 0, bytes.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream() ?? throw new InvalidOperationException()))
                {
                    var json = sr.ReadToEnd();
                    return ParseClaimResponse(json);
                }
            }
            catch (WebException webEx)
            {
                var http = webEx.Response as HttpWebResponse;
                AppendLog("    HTTP status: " + (http != null ? ((int)http.StatusCode).ToString() : "no-response"));
                return null;
            }
            catch (Exception ex)
            {
                AppendLog("    claim exception: " + ex.Message);
                return null;
            }
        }

        // Minimal JSON extractor — üçüncü parti kütüphaneye dokunmamak için
        // sadece ihtiyaç duyduğumuz dört alanı string match ile çekiyoruz.
        private static ClaimResponse ParseClaimResponse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return new ClaimResponse
            {
                DeviceToken = JsonField(json, "deviceToken"),
                CompanyName = JsonField(json, "companyName"),
                CompanyId = JsonField(json, "companyId"),
                DeviceId = int.TryParse(JsonField(json, "deviceId"), out var id) ? id : 0,
            };
        }

        private static string JsonField(string json, string name)
        {
            // "name":"value" veya "name":123 ikisini de yakalar
            var key = "\"" + name + "\"";
            var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;
            var colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return string.Empty;
            var i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length) return string.Empty;
            if (json[i] == '"')
            {
                i++;
                var end = i;
                var sb = new StringBuilder();
                while (end < json.Length && json[end] != '"')
                {
                    if (json[end] == '\\' && end + 1 < json.Length) { sb.Append(json[end + 1]); end += 2; }
                    else { sb.Append(json[end]); end++; }
                }
                return sb.ToString();
            }
            var endNum = i;
            while (endNum < json.Length && (char.IsDigit(json[endNum]) || json[endNum] == '.' || json[endNum] == '-'))
                endNum++;
            return json.Substring(i, endNum - i);
        }

        private void SendPing()
        {
            try
            {
                var url = _apiBase.TrimEnd('/') + "/caller-id/ping";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["Authorization"] = "Bearer " + _deviceToken;
                req.ContentLength = 0;
                req.Timeout = 10000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    _isConnected = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
                UpdateStatusLabel();
                UpdateTrayIcon();
            }
            catch (WebException webEx)
            {
                _isConnected = false;
                var http = webEx.Response as HttpWebResponse;
                AppendLog("Ping hatası: HTTP " + (http != null ? ((int)http.StatusCode).ToString() : "no-response"));
                UpdateStatusLabel();
                UpdateTrayIcon();
            }
            catch (Exception ex)
            {
                _isConnected = false;
                AppendLog("Ping exception: " + ex.Message);
                UpdateStatusLabel();
                UpdateTrayIcon();
            }
        }

        private class ClaimResponse
        {
            public int DeviceId;
            public string DeviceToken;
            public string CompanyName;
            public string CompanyId;
        }

        private void RefreshDeviceStatus()
        {
            try
            {
                var model = _cid.Command("Devicemodel") ?? string.Empty;
                var serial = _cid.Command("Serial") ?? string.Empty;
                _deviceLabel.Text = $"Cihaz: model='{model}' serial='{serial}'";
            }
            catch (Exception ex)
            {
                _deviceLabel.Text = "Cihaz sorgu hatası: " + ex.Message;
            }
        }

        private void Cid_OnCallerID(object sender, Axcidv5callerid.ICIDv5Events_OnCallerIDEvent e)
        {
            string phone = string.Empty;
            try { phone = e.phoneNumber ?? string.Empty; }
            catch { /* some COM builds throw on accessor */ }

            AppendLog($"[COM OnCallerID fire] phone='{phone}' line='{SafeProp(e, "line")}' dt='{SafeProp(e, "dateTime")}' deviceSerial='{SafeProp(e, "deviceSerial")}'");

            if (string.IsNullOrWhiteSpace(phone))
            {
                AppendLog("  → phoneNumber boş geldi, ingest atlanıyor");
                return;
            }

            // Basic normalization mirroring NegroPos
            if (phone.Length >= 2 && phone.StartsWith("09"))
                phone = phone.Substring(2);
            if (phone.Length == 12)
                phone = phone.Substring(1, 11);

            AppendLog($"  → normalized phone='{phone}'");

            if (string.IsNullOrEmpty(_deviceToken))
            {
                AppendLog("  → token yok, backend'e gönderilmedi");
                return;
            }

            try
            {
                var ok = PostIngest(phone);
                AppendLog(ok ? "  ✓ /caller-id/ingest başarılı" : "  ✗ /caller-id/ingest başarısız");
            }
            catch (Exception ex)
            {
                AppendLog("  ✗ ingest exception: " + ex.Message);
            }
        }

        private static string SafeProp(object obj, string name)
        {
            try { return obj.GetType().GetProperty(name)?.GetValue(obj, null)?.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private bool PostIngest(string phone)
        {
            var url = _apiBase.TrimEnd('/') + "/caller-id/ingest";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Authorization"] = "Bearer " + _deviceToken;
            req.Timeout = 15000;

            var body = "{\"phoneNumber\":\"" + JsonEscape(phone) + "\",\"source\":\"usb\"}";
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream())
                s.Write(bytes, 0, bytes.Length);

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
            }
            catch (WebException webEx)
            {
                var http = webEx.Response as HttpWebResponse;
                AppendLog("    HTTP status: " + (http != null ? ((int)http.StatusCode).ToString() : "no-response"));
                return false;
            }
        }

        private static string JsonEscape(string s) =>
            (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private void AppendLog(string line)
        {
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            if (_logList.InvokeRequired) _logList.Invoke((Action)(() => _logList.Items.Insert(0, stamped)));
            else _logList.Items.Insert(0, stamped);
            while (_logList.Items.Count > 500) _logList.Items.RemoveAt(_logList.Items.Count - 1);
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}\r\n");
            }
            catch { /* non-fatal */ }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (var line in File.ReadAllLines(ConfigPath))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    if (key == "deviceToken") _deviceToken = val;
                    else if (key == "apiBase") _apiBase = val;
                }
            }
            catch { /* best-effort */ }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigPath,
                    $"deviceToken={_deviceToken}\r\napiBase={_apiBase}\r\n");
            }
            catch { /* best-effort */ }
        }
    }
}
