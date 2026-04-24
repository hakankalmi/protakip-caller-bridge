using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net;
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
            _saveTokenBtn.Click += (_, __) =>
            {
                _deviceToken = _tokenBox.Text.Trim();
                SaveConfig();
                AppendLog("Token kaydedildi (" + _deviceToken.Length + " karakter)");
                _statusLabel.Text = string.IsNullOrEmpty(_deviceToken)
                    ? "Token gerekli — yapıştırıp Kaydet'e basın"
                    : "Dinleniyor";
            };
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

            _statusLabel.Text = string.IsNullOrEmpty(_deviceToken)
                ? "Token gerekli — yapıştırıp Kaydet'e basın"
                : "Dinleniyor";

            // ActiveX'ten cihaz bilgisi alıp her saniye status güncelle.
            var tick = new Timer { Interval = 1000 };
            tick.Tick += (_, __) => RefreshDeviceStatus();
            tick.Start();
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
