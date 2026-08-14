using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

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
        // ActiveX yalnızca "com" (CID v5/v6) modunda oluşturulur — "cid"
        // (eski cihaz) modunda null kalır.
        private Axcidv5callerid.AxCIDv5 _cid;
        private readonly ListBox _logList;
        private readonly Label _statusLabel;
        private readonly Label _deviceLabel;
        private readonly TextBox _tokenBox;
        private readonly Button _saveTokenBtn;
        private NotifyIcon _tray;
        private bool _reallyExit;
        private string _apiBase = "https://api.protakip.com/api";
        private string _deviceToken = string.Empty;

        // Cihaz türü: "com" = CID v5/v6 (ActiveX COM), "cid" = eski C812A/C814A
        // (cidshow cid.dll). Config'e kaydedilir, açılışta ilgili dinleyici
        // başlatılır. Tür değişince temiz yeniden başlatma yapılır.
        private string _deviceMode = "com";
        private RadioButton _rbCom;
        private RadioButton _rbCid;
        private string _lastCidModel = string.Empty;
        private string _lastCidSerial = string.Empty;

        // ── Yanlış cihaz türü / cihazı başkası tutuyor teşhisi ───────────
        // Ankara Güven Halı Yıkama vakası (14.08.2026): CID v6 cihaz "Eski
        // cihaz" modunda seçiliydi. cid.dll o cihazda Signal fire ediyor
        // (model + seri no ekranda GÖRÜNÜYOR, "Dinleniyor (Bağlı)" yazıyor)
        // ama CallerID ASLA fire etmiyor → arayan yakalanmıyor. Ekran
        // tamamen sağlıklı göründüğü için teşhis saatler sürdü. Artık köprü
        // bunu kendi tespit edip bir kez otomatik doğru moda geçiyor.
        private bool _autoSwitchedToCom;   // config'e yazılır — ping-pong restart engeli
        private bool _wrongModeWarned;
        private bool _multiDeviceWarned;
        private int _comNoDeviceTicks;     // COM modunda cihazın görünmediği saniye
        private bool _comNoDeviceWarned;
        private string _warning;           // doluysa status etiketinde kırmızı gösterilir

        // NetGSM TCP subscriber state. Pair sonrası /caller-id/pbx-config
        // çekilir, enabled + provider=netgsm ise socket açılır. Her ping'te
        // version alanı tekrar sorulur; değişmişse subscriber stop edilip
        // yeni credentials ile yeniden başlatılır.
        private NetgsmTcpClient _netgsm;
        private string _netgsmVersion;

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProTakipCallerBridgeCom");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.ini");
        private static readonly string LogPath = Path.Combine(ConfigDir, "bridge.log");

        // CI build'i 1.0.0.<run_number> ile damgalar; form başlığında + log'da
        // gösterilir ki Hakan hangi build'in çalıştığını teyit edebilsin.
        private static string AppVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";

        public MainForm()
        {
            Program.LogLine("MainForm ctor: başladı");
            CheckForIllegalCrossThreadCalls = false;

            Directory.CreateDirectory(ConfigDir);
            LoadConfig();
            Program.LogLine("MainForm ctor: config yüklendi (token len=" + _deviceToken.Length +
                ", deviceMode=" + _deviceMode + ")");

            Text = "ProTakip Caller Id — COM  v" + AppVersion;
            ClientSize = new Size(640, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Program.LogLine("MainForm ctor: form özellikleri set edildi");

            // Cihaz dinleyicisi artık ctor'da eager başlatılmıyor — UI kurulduktan
            // sonra seçili moda göre ActivateMode() içinde başlatılıyor (aşağıda).

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

            // ── Cihaz türü seçimi ───────────────────────────────────────
            // İki USB caller-ID cihaz ailesi iki farklı sürücü yolu kullanır;
            // tek sürücü ikisini de yakalayamaz:
            //   • Yeni cihaz (CID v5/v6) → cidv5callerid ActiveX COM
            //   • Eski cihaz (C812A/C814A) → cidshow cid.dll SetEvents
            // Kullanıcı cihazına uyan türü seçer; seçim config'e kaydedilir.
            // Tür değişince temiz yeniden başlatma yapılır (cid.dll unhook
            // edilemiyor + iki sürücü aynı USB cihazını paylaşamıyor).
            var modeGroup = new GroupBox
            {
                Text = "Cihaz Türü",
                Location = new Point(12, 68),
                Size = new Size(616, 64),
                Font = new Font("Segoe UI", 9f),
            };
            _rbCom = new RadioButton
            {
                Text = "Yeni cihaz (CID v5 / v6)",
                Location = new Point(16, 26),
                Size = new Size(280, 24),
                Checked = _deviceMode != "cid",
            };
            _rbCid = new RadioButton
            {
                Text = "Eski cihaz (C812A / C814A)",
                Location = new Point(312, 26),
                Size = new Size(280, 24),
                Checked = _deviceMode == "cid",
            };
            _rbCom.CheckedChanged += (_, __) => OnDeviceModeChanged();
            _rbCid.CheckedChanged += (_, __) => OnDeviceModeChanged();
            modeGroup.Controls.Add(_rbCom);
            modeGroup.Controls.Add(_rbCid);
            Controls.Add(modeGroup);

            var tokenTitle = new Label
            {
                Text = "Eşleşme kodu / Token (pair sonrası yapıştırın):",
                Location = new Point(12, 142),
                Size = new Size(400, 20),
            };
            Controls.Add(tokenTitle);

            // Tek tıkla tam tanı — destek konuşmasında "şu satırda ne yazıyor,
            // log'u atar mısın, hangi programlar açık" turlarını tek yapıştırmaya
            // indirir. Panoya kopyalar, Hakan WhatsApp'a yapıştırır.
            var diagBtn = new Button
            {
                Text = "Tanıyı Kopyala",
                Location = new Point(492, 138),
                Size = new Size(136, 25),
            };
            diagBtn.Click += (_, __) => CopyDiagnostics();
            Controls.Add(diagBtn);

            // PlaceholderText .NET 5+ — net40'ta yok.
            _tokenBox = new TextBox
            {
                Location = new Point(12, 164),
                Size = new Size(520, 23),
                Text = _deviceToken,
            };
            Controls.Add(_tokenBox);

            _saveTokenBtn = new Button
            {
                Text = "Kaydet",
                Location = new Point(540, 163),
                Size = new Size(88, 25),
            };
            _saveTokenBtn.Click += (_, __) => OnSaveClicked();
            Controls.Add(_saveTokenBtn);

            _logList = new ListBox
            {
                Location = new Point(12, 200),
                Size = new Size(616, 308),
                IntegralHeight = false,
                Font = new Font("Consolas", 9f),
            };
            Controls.Add(_logList);

            AppendLog("=== Bridge COM başladı — v" + AppVersion + " (net48)");
            AppendLog("Config: " + ConfigPath);
            AppendLog("Log: " + LogPath);

            // Seçili cihaz türünün dinleyicisini başlat.
            ActivateMode();

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
                // Yalnızca kullanıcı X'e bastığında tray'e gizle. Application
                // .Restart() / sistem kapanışı (ApplicationExitCall, WindowsShut
                // Down) gerçek kapanmadır — engellersek yeniden başlatma takılır.
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                HideToTray(showBalloon: true);
            };
        }

        // ── Cihaz türü modu ──────────────────────────────────────────────

        /// <summary>Seçili moda göre ilgili dinleyiciyi başlatır.</summary>
        private void ActivateMode()
        {
            if (_deviceMode == "cid") StartCidMode();
            else StartComMode();
        }

        /// <summary>
        /// Yeni cihaz (CID v5/v6) — cidv5callerid ActiveX COM kontrolü. DLL
        /// regsvr32 ile kayıtlı değilse hata logu + uyarı, ama process'i
        /// ÖLDÜRMEZ (eski cihaz kullanıcısı COM DLL'sine sahip olmayabilir,
        /// türü değiştirip devam edebilmeli).
        /// </summary>
        private void StartComMode()
        {
            try
            {
                Program.LogLine("StartComMode: ActiveX instantiating Axcidv5callerid.AxCIDv5");
                _cid = new Axcidv5callerid.AxCIDv5
                {
                    Visible = false,
                    Location = new Point(0, 0),
                    Size = new Size(10, 10),
                };
                ((ISupportInitialize)_cid).BeginInit();
                Controls.Add(_cid);
                ((ISupportInitialize)_cid).EndInit();
                _cid.OnCallerID += Cid_OnCallerID;

                // NegroPos pattern — Hide+Start yapmadan ActiveX cihazla
                // iletişime geçmiyor, Command() boş dönüyor, OnCallerID asla
                // fire etmiyor. Start() kritik.
                _cid.Hide();
                _cid.Start();
                Program.LogLine("StartComMode: ActiveX Start() OK — CID v5/v6 dinleniyor");
                AppendLog("Yeni cihaz (CID v5/v6) modu aktif — dinleniyor");

                // Başlangıç teşhisi log'a — "cihaz görünmüyor" şikâyetinde
                // bridge.log tek başına sebebi söylesin: sürücü kayıtlı mı,
                // cihaz ilk sorguda ne dönüyor, rakip süreç açık mı.
                Program.LogLine("StartComMode: HKCR CIDv5CallerID.CIDv5 present=" + IsCidv5Registered());
                Program.LogLine("StartComMode: ilk sorgu → model='" + SafeCommand("Devicemodel") +
                    "' serial='" + SafeCommand("Serial") + "'");
                Program.LogLine("StartComMode: cihazı tutabilecek süreçler: " + ListCompetingProcesses());
            }
            catch (Exception ex)
            {
                Program.LogLine("StartComMode FAILED: " + ex.GetType().Name + ": " + ex.Message);
                AppendLog("✗ COM bileşeni yüklenemedi: " + ex.Message);
                AppendLog("  → cidv5callerid.dll kayıtlı değil olabilir. register.bat'ı yönetici çalıştırın.");
                MessageBox.Show(
                    "Caller ID COM bileşeni yüklenemedi:\n\n" + ex.Message +
                    "\n\ncidv5callerid.dll sistemde kayıtlı değil. register.bat'a sağ tık → " +
                    "Yönetici olarak çalıştır.\n\nEski cihaz kullanıyorsanız 'Eski cihaz' türünü seçin.",
                    "ProTakip Caller Id — COM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _cid = null;
            }
        }

        /// <summary>
        /// Eski cihaz (C812A/C814A) — cidshow cid.dll SetEvents yolu. Native
        /// callback'ler ayrı thread'den gelir; CallerID UI thread'e marshal
        /// edilir, Signal yalnızca durum için saklanır.
        /// </summary>
        private void StartCidMode()
        {
            try
            {
                CidInterop.SetEvents(OnCid_CallerID, OnCid_Signal);
                Program.LogLine("StartCidMode: cid.dll SetEvents hooked — C812A/C814A dinleniyor");
                AppendLog("Eski cihaz (cid.dll) modu aktif — dinleniyor");
            }
            catch (Exception ex)
            {
                Program.LogLine("StartCidMode FAILED: " + ex.GetType().Name + ": " + ex.Message);
                AppendLog("✗ cid.dll yüklenemedi: " + ex.Message);
                MessageBox.Show(
                    "Eski cihaz sürücüsü (cid.dll) yüklenemedi:\n\n" + ex.Message,
                    "ProTakip Caller Id",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Radyo değişince: yeni modu config'e yaz, uygulamayı temiz yeniden
        /// başlat. cid.dll SetEvents geri alınamadığı ve iki sürücü aynı USB
        /// cihazını paylaşamadığı için süreç içi geçiş güvenli değil.
        /// </summary>
        private void OnDeviceModeChanged()
        {
            var newMode = _rbCid.Checked ? "cid" : "com";
            if (newMode == _deviceMode) return;

            _deviceMode = newMode;
            SaveConfig();
            AppendLog("Cihaz türü değişti → " + (newMode == "cid"
                ? "Eski cihaz (cid.dll)" : "Yeni cihaz (CID v5/v6)") +
                " — temiz başlatma için yeniden başlatılıyor…");
            Program.LogLine("Device mode changed to " + newMode + " — restarting");

            // Kullanıcı değişimi görsün diye kısa gecikme, sonra temiz restart.
            var t = new Timer { Interval = 700 };
            t.Tick += (_, __) =>
            {
                t.Stop();
                _reallyExit = true;                 // FormClosing tray'e gizlemesin
                if (_tray != null) _tray.Visible = false;
                Application.Restart();
            };
            t.Start();
        }

        // cidshow cid.dll native thread'inden gelir — UI thread'e marshal et.
        private void OnCid_CallerID(string deviceSerial, string line, string phoneNumber,
            string dateTime, string other)
        {
            Program.LogLine("[CID native OnCallerID] phone='" + phoneNumber + "' line='" + line +
                "' serial='" + deviceSerial + "'");
            try
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)(() => HandleIncomingCaller(phoneNumber, "cid")));
                else
                    HandleIncomingCaller(phoneNumber, "cid");
            }
            catch (Exception ex) { Program.LogLine("OnCid_CallerID marshal failed: " + ex.Message); }
        }

        // Signal her ~1 sn fire eder — log'u boğmamak için yalnızca cihaz
        // kimliğini sakla, durum etiketini RefreshDeviceStatus günceller.
        private void OnCid_Signal(string deviceModel, string deviceSerial,
            int s1, int s2, int s3, int s4)
        {
            if (!string.IsNullOrEmpty(deviceSerial)) _lastCidSerial = deviceSerial;
            if (!string.IsNullOrEmpty(deviceModel)) _lastCidModel = deviceModel;

            // Eski cihaz modundayız ama cihaz YENİ nesil (CID v5/v6) diyor →
            // bu yolda arayan numara asla gelmez. Native thread'den geliyoruz,
            // UI thread'e marshal et.
            if (_deviceMode == "cid" && !_wrongModeWarned && IsNewDeviceModel(deviceModel))
            {
                _wrongModeWarned = true;
                try
                {
                    if (IsHandleCreated) BeginInvoke((Action)HandleWrongDeviceMode);
                    else HandleWrongDeviceMode();
                }
                catch (Exception ex) { Program.LogLine("wrong-mode marshal failed: " + ex.Message); }
            }

            WarnIfMultipleDevices(deviceSerial);
        }

        /// <summary>
        /// cid.dll Signal'inden gelen model adı yeni nesil cihazı mı gösteriyor?
        /// Örnekler: "CID v6", "CID v5a", iki cihazda "CID v6,CID v6".
        /// Eski aile ise "C812A" / "C814A" döner ve bu false'tur.
        /// </summary>
        private static bool IsNewDeviceModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            var m = model.ToLowerInvariant();
            return m.Contains("v5") || m.Contains("v6");
        }

        /// <summary>
        /// Yanlış cihaz türü tespit edildi: bir kez otomatik olarak doğru moda
        /// geçip yeniden başlat. İkinci kez tespit edilirse (kullanıcı elle geri
        /// almışsa) sadece uyar — restart döngüsüne girmeyelim.
        /// </summary>
        private void HandleWrongDeviceMode()
        {
            AppendLog("⚠ Cihaz YENİ nesil görünüyor (model='" + _lastCidModel + "').");
            AppendLog("   'Eski cihaz' modunda bu cihazda arayan numara YAKALANMAZ —" +
                      " cihaz görünür ama çağrı gelmez.");

            if (_autoSwitchedToCom)
            {
                SetWarning("⚠ Cihaz türü yanlış: bu cihaz 'Yeni cihaz (CID v5/v6)' olmalı");
                return;
            }

            _autoSwitchedToCom = true;
            _deviceMode = "com";
            SaveConfig();
            AppendLog("   → Otomatik olarak 'Yeni cihaz (CID v5/v6)' moduna geçiliyor," +
                      " uygulama yeniden başlatılıyor…");
            Program.LogLine("Auto-switch cid → com (model='" + _lastCidModel + "')");

            var t = new Timer { Interval = 1500 };
            t.Tick += (_, __) =>
            {
                t.Stop();
                _reallyExit = true;
                if (_tray != null) _tray.Visible = false;
                Application.Restart();
            };
            t.Start();
        }

        /// <summary>
        /// Seri no virgüllü geliyorsa aynı PC'ye birden fazla caller-ID kutusu
        /// takılıdır (Güven Halı: 2 kutu × 2 hat = 4 telefon). Sürücü tek kutuyu
        /// dinliyor olabilir — kullanıcı her hattan test etmeli.
        /// </summary>
        private void WarnIfMultipleDevices(string serial)
        {
            if (_multiDeviceWarned || string.IsNullOrEmpty(serial) || serial.IndexOf(',') < 0) return;
            _multiDeviceWarned = true;
            AppendLog("ℹ Birden fazla cihaz bağlı (seri: " + serial + ").");
            AppendLog("   Sürücü yalnızca birini dinliyor olabilir — HER hattan test araması yapın.");
        }

        /// <summary>Status etiketinde kalıcı kırmızı uyarı gösterir.</summary>
        private void SetWarning(string text)
        {
            _warning = text;
            UpdateStatusLabel();
        }

        /// <summary>
        /// Cihazı aynı anda tek uygulama tutabildiği için, köprü cihazı
        /// göremediğinde ilk sorulacak soru "başka ne açık?" oluyor. Bilinen
        /// rakip süreçleri isimden tarayıp tanıya yazıyoruz.
        /// </summary>
        private static string ListCompetingProcesses()
        {
            string[] needles = { "negropos", "cihaz", "cidshow", "cid", "caller" };
            var found = new StringBuilder();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    if (string.Equals(n, "ProTakipCallerBridgeCom", StringComparison.OrdinalIgnoreCase)) continue;
                    var low = n.ToLowerInvariant();
                    foreach (var needle in needles)
                    {
                        if (low.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                        if (found.Length > 0) found.Append(", ");
                        found.Append(n);
                        break;
                    }
                }
            }
            catch (Exception ex) { return "(taranamadı: " + ex.Message + ")"; }
            return found.Length == 0 ? "(yok)" : found.ToString();
        }

        private static bool IsCidv5Registered()
        {
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey("CIDv5CallerID.CIDv5"))
                    return k != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Destek için tam durum dökümü — panoya kopyalanır. Cihaz görünmüyorsa
        /// sebebi ayırt eden her şey burada: COM bileşeni oluştu mu, sürücü
        /// kayıtlı mı, Command() ne dönüyor, cihazı tutan başka süreç var mı.
        /// </summary>
        private void CopyDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ProTakip Caller Id — Tanı");
            sb.AppendLine("Sürüm      : " + AppVersion + " (net48/x86)");
            sb.AppendLine("Zaman      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Bilgisayar : " + Environment.MachineName + " · " + Environment.OSVersion);
            sb.AppendLine("Cihaz türü : " + (_deviceMode == "cid"
                ? "Eski cihaz (cid.dll)" : "Yeni cihaz (CID v5/v6 COM)") +
                (_autoSwitchedToCom ? "  [otomatik geçiş yapıldı]" : ""));
            sb.AppendLine("Token      : " + (_deviceToken.Length == 0
                ? "YOK" : _deviceToken.Length + " karakter") + " · ping=" + (_isConnected ? "Bağlı" : "BAĞLI DEĞİL"));

            if (_deviceMode == "cid")
            {
                sb.AppendLine("cid.dll    : model='" + _lastCidModel + "' serial='" + _lastCidSerial + "'");
                if (IsNewDeviceModel(_lastCidModel))
                    sb.AppendLine("  ⚠ Cihaz YENİ nesil — bu modda arayan numara YAKALANMAZ.");
            }
            else
            {
                sb.AppendLine("COM nesnesi: " + (_cid == null ? "OLUŞMADI (register.bat gerekli)" : "oluştu"));
                sb.AppendLine("Sürücü kaydı (HKCR\\CIDv5CallerID.CIDv5): " + (IsCidv5Registered() ? "VAR" : "YOK"));
                if (_cid != null)
                {
                    string model = "?", serial = "?";
                    try { model = _cid.Command("Devicemodel") ?? string.Empty; } catch (Exception ex) { model = "HATA: " + ex.Message; }
                    try { serial = _cid.Command("Serial") ?? string.Empty; } catch (Exception ex) { serial = "HATA: " + ex.Message; }
                    sb.AppendLine("COM cihaz  : model='" + model + "' serial='" + serial + "'");
                    if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(serial))
                        sb.AppendLine("  ⚠ COM cihazı görmüyor — cihazı tutan başka program veya USB sorunu.");
                }
            }

            sb.AppendLine("Cihazı tutabilecek açık programlar: " + ListCompetingProcesses());
            sb.AppendLine("Log dosyası: " + LogPath);
            sb.AppendLine();
            sb.AppendLine("── Son kayıtlar ──");
            for (int i = 0; i < _logList.Items.Count && i < 30; i++)
                sb.AppendLine(_logList.Items[i].ToString());

            try
            {
                Clipboard.SetText(sb.ToString());
                AppendLog("✓ Tanı panoya kopyalandı — destek görüşmesine yapıştırabilirsiniz.");
                MessageBox.Show(
                    "Tanı bilgisi panoya kopyalandı.\n\nWhatsApp'ta ProTakip destek hattına yapıştırıp gönderin.",
                    "ProTakip Caller Id", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("Tanı kopyalanamadı: " + ex.Message);
            }
        }

        /// <summary>
        /// COM ve cid.dll yolları aynı normalize + /caller-id/ingest mantığını
        /// paylaşır. Daima UI thread'inde çağrılmalı.
        /// </summary>
        private void HandleIncomingCaller(string rawPhone, string source)
        {
            var phone = (rawPhone ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(phone))
            {
                AppendLog("[" + source + "] phoneNumber boş geldi, ingest atlanıyor");
                return;
            }

            // NegroPos normalizasyonu — 09 prefix ve 12 haneli formu sadeleştir.
            if (phone.Length >= 2 && phone.StartsWith("09")) phone = phone.Substring(2);
            if (phone.Length == 12) phone = phone.Substring(1, 11);

            AppendLog("[" + source + " OnCallerID] arayan='" + phone + "'");

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

        // Tray icon renk durumları — bridge genel sağlığına göre değişir.
        private enum TrayState { Pending, Ok, Error }

        // 3 durum için icon'ları cache'le. Her state değişiminde yeniden
        // Bitmap+GetHicon yapmak GDI handle leak'i yapıyordu ve eski icon'u
        // Dispose etmek form title bar'ının da aynı icon'a referans etmesi
        // yüzünden ObjectDisposedException fırlatıyordu ("Bırakılmış nesne:
        // Icon"). Bu cache ile her icon ömür boyu yaşar, Dispose()'da toplu
        // temizlenir.
        private Icon _iconPending;
        private Icon _iconOk;
        private Icon _iconError;

        private Icon GetCachedTrayIcon(TrayState s)
        {
            switch (s)
            {
                case TrayState.Ok:
                    if (_iconOk == null) _iconOk = BuildTrayIcon(TrayState.Ok);
                    return _iconOk;
                case TrayState.Error:
                    if (_iconError == null) _iconError = BuildTrayIcon(TrayState.Error);
                    return _iconError;
                default:
                    if (_iconPending == null) _iconPending = BuildTrayIcon(TrayState.Pending);
                    return _iconPending;
            }
        }

        private void InitTray()
        {
            // Tray ikonunu runtime'da çiz: yeşil/amber/kırmızı daire üstünde
            // beyaz telefon glyph. Küçük bir asset dosyası paketlemekten
            // kaçınıyoruz, Windows 16/20/24 px'e downscale ediyor.
            var trayIcon = GetCachedTrayIcon(TrayState.Pending);
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
            if (disposing)
            {
                try { _netgsm?.Dispose(); } catch { }
                _netgsm = null;
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
                // Icon cache temizliği. base.Dispose() form.Icon'u kendisi
                // release ediyor; biz sadece tray + geri kalan state'leri
                // dispose ediyoruz. Hepsi GDI handle — leak olmasın.
                try { _iconPending?.Dispose(); } catch { }
                try { _iconOk?.Dispose(); } catch { }
                try { _iconError?.Dispose(); } catch { }
                _iconPending = _iconOk = _iconError = null;
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

            // Cache'den al, eski icon'u DISPOSE ETME — form title bar aynı
            // referansı tutuyor, dispose edilirse hide/paint sırasında
            // ObjectDisposedException fırlatıyor.
            _tray.Icon = GetCachedTrayIcon(s);
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
            // Uyarı varsa her şeyin önüne geçer. "Dinleniyor (Bağlı)" yazısı
            // yanlış moddayken de yeşil göründüğü için kullanıcı sorunu
            // göremiyordu — uyarı bu satırı devralır.
            if (!string.IsNullOrEmpty(_warning))
            {
                _statusLabel.Text = _warning;
                _statusLabel.ForeColor = Color.FromArgb(185, 28, 28); // red-700
                return;
            }

            _statusLabel.ForeColor = SystemColors.ControlText;
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

        private string SafeCommand(string cmd)
        {
            try { return _cid != null ? (_cid.Command(cmd) ?? string.Empty) : "(COM yok)"; }
            catch (Exception ex) { return "HATA: " + ex.Message; }
        }

        private string SafeGetSerial()
        {
            try
            {
                if (_deviceMode == "cid") return _lastCidSerial ?? string.Empty;
                return _cid != null ? (_cid.Command("Serial") ?? string.Empty) : string.Empty;
            }
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

                // Ping başarılı → PBX config'i de kontrol et. Eski .NET 8
                // bridge'de bu akış vardı, bridge-com'a port edildi. NetGSM
                // müşterileri ping'te subscriber'ın otomatik kurulduğunu
                // görüyor; version değişimi anında yeniden bağlantı.
                try { RefreshPbxConfig(); } catch (Exception ex) { AppendLog("PBX config refresh: " + ex.Message); }
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

        // ── NetGSM PBX config ────────────────────────────────────────────

        private class PbxConfigResponse
        {
            public bool Enabled;
            public string Provider;
            public string Host;
            public int Port;
            public string Username;
            public string Password;
            public string Version;
        }

        /// <summary>
        /// GET /caller-id/pbx-config — müşterinin NetGSM kredilerini çeker.
        /// Version değişmişse mevcut subscriber'ı durdurup yeniden kuruyoruz
        /// (web panelden Username/Password güncellemesi anında etki etsin).
        /// Enabled=false veya provider ≠ netgsm → aktif subscriber varsa durdur.
        /// Ağ hatasında sessiz — mevcut socket devam eder, sonraki ping'te
        /// tekrar denenecek.
        /// </summary>
        private void RefreshPbxConfig()
        {
            if (string.IsNullOrEmpty(_deviceToken)) return;

            var url = _apiBase.TrimEnd('/') + "/caller-id/pbx-config";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + _deviceToken;
            req.Timeout = 10000;

            PbxConfigResponse cfg = null;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream() ?? throw new InvalidOperationException()))
                {
                    var json = sr.ReadToEnd();
                    cfg = ParsePbxConfig(json);
                }
            }
            catch
            {
                // 401/404/network — sessiz. Subscriber mevcut haliyle devam.
                return;
            }

            if (cfg == null) return;

            var wantsNetgsm = cfg.Enabled &&
                              string.Equals(cfg.Provider, "netgsm", StringComparison.OrdinalIgnoreCase) &&
                              !string.IsNullOrEmpty(cfg.Host) &&
                              cfg.Port > 0 &&
                              !string.IsNullOrEmpty(cfg.Username);

            if (!wantsNetgsm)
            {
                // Pasif veya farklı sağlayıcı → mevcut varsa kapat.
                if (_netgsm != null)
                {
                    AppendLog("NetGSM devre dışı, TCP kapatılıyor");
                    try { _netgsm.Dispose(); } catch { }
                    _netgsm = null;
                    _netgsmVersion = null;
                }
                return;
            }

            // Aynı version + aynı subscriber zaten çalışıyor → dokunma.
            if (_netgsm != null && _netgsmVersion == cfg.Version) return;

            // Değişmiş veya yeni → eski subscriber'ı kapat, yenisini kur.
            if (_netgsm != null)
            {
                AppendLog(string.Format("NetGSM config değişti (version {0} → {1}), TCP yeniden kuruluyor",
                    _netgsmVersion ?? "-", cfg.Version ?? "-"));
                try { _netgsm.Dispose(); } catch { }
                _netgsm = null;
            }

            AppendLog(string.Format("NetGSM TCP başlatılıyor: {0}:{1}", cfg.Host, cfg.Port));
            var subscriber = new NetgsmTcpClient(
                host: cfg.Host,
                port: cfg.Port,
                username: cfg.Username,
                password: cfg.Password ?? string.Empty,
                version: cfg.Version ?? string.Empty,
                onIncomingNumber: OnNetgsmIncomingAsync,
                log: msg => AppendLog(msg));
            subscriber.Start();
            _netgsm = subscriber;
            _netgsmVersion = cfg.Version;
        }

        private System.Threading.Tasks.Task OnNetgsmIncomingAsync(string phoneNumber)
        {
            // NetGSM event'lerini aynı USB OnCallerID yoluyla işle: normalize
            // + /caller-id/ingest POST. UI thread'e marshal gerek yok, ingest
            // zaten HttpWebRequest ile senkron çalışıyor.
            try
            {
                var phone = (phoneNumber ?? string.Empty).Trim();
                if (phone.Length >= 2 && phone.StartsWith("09")) phone = phone.Substring(2);
                if (phone.Length == 12) phone = phone.Substring(1, 11);

                AppendLog(string.Format("[NetGSM ring] phone='{0}'", phone));
                if (string.IsNullOrEmpty(phone)) return System.Threading.Tasks.Task.FromResult(0);

                if (string.IsNullOrEmpty(_deviceToken))
                {
                    AppendLog("  → token yok, ingest atlanıyor");
                    return System.Threading.Tasks.Task.FromResult(0);
                }

                var ok = PostIngest(phone);
                AppendLog(ok ? "  ✓ /caller-id/ingest (netgsm) başarılı"
                             : "  ✗ /caller-id/ingest (netgsm) başarısız");
            }
            catch (Exception ex)
            {
                AppendLog("NetGSM ingest exception: " + ex.Message);
            }
            return System.Threading.Tasks.Task.FromResult(0);
        }

        private static PbxConfigResponse ParsePbxConfig(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var r = new PbxConfigResponse();
            r.Enabled = ParseBoolField(json, "enabled");
            r.Provider = JsonField(json, "provider");
            r.Host = JsonField(json, "host");
            var portStr = JsonField(json, "port");
            int port;
            r.Port = int.TryParse(portStr, out port) ? port : 0;
            r.Username = JsonField(json, "username");
            r.Password = JsonField(json, "password");
            r.Version = JsonField(json, "version");
            return r;
        }

        private static bool ParseBoolField(string json, string name)
        {
            // "enabled":true / "enabled":false — basit string arama
            var key = "\"" + name + "\"";
            var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return false;
            var rest = json.Substring(colon + 1).TrimStart();
            return rest.StartsWith("true", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshDeviceStatus()
        {
            try
            {
                if (_deviceMode == "cid")
                {
                    if (string.IsNullOrEmpty(_lastCidModel) && string.IsNullOrEmpty(_lastCidSerial))
                        _deviceLabel.Text = "Cihaz (eski / cid.dll): sinyal bekleniyor…";
                    else
                        _deviceLabel.Text = "Cihaz (eski / cid.dll): model='" + _lastCidModel +
                            "' serial='" + _lastCidSerial + "'";
                    return;
                }

                if (_cid == null)
                {
                    _deviceLabel.Text = "Cihaz (yeni / COM): bileşen yüklenemedi — register.bat çalıştırın";
                    return;
                }

                var model = _cid.Command("Devicemodel") ?? string.Empty;
                var serial = _cid.Command("Serial") ?? string.Empty;
                _deviceLabel.Text = $"Cihaz (yeni / COM): model='{model}' serial='{serial}'";

                if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(serial))
                {
                    // Cihaz 15 sn boyunca hiç görünmedi. En sık iki sebep:
                    // (1) cihazı BAŞKA bir program tutuyor — sürücü aynı anda
                    //     tek uygulamaya bağlanıyor (NegroPos, "Cihaz Test",
                    //     cidshow açıksa köprüye sıra gelmez),
                    // (2) cihaz gerçekten eski aile (C812A/C814A).
                    if (++_comNoDeviceTicks >= 15 && !_comNoDeviceWarned)
                    {
                        _comNoDeviceWarned = true;
                        SetWarning("⚠ Cihaz görünmüyor — başka program tutuyor olabilir");
                        AppendLog("⚠ 15 sn'dir cihaz görünmüyor.");
                        AppendLog("   1) Cihazı tutan diğer programları KAPATIN " +
                                  "(NegroPos, 'Cihaz Test', cidshow) — cihaz aynı anda tek programa bağlanır.");
                        AppendLog("   2) USB kablosunu çıkarıp başka porta takın.");
                        AppendLog("   3) Cihazınız eski model (C812A/C814A) ise cihaz türünü değiştirin.");
                    }
                }
                else
                {
                    _comNoDeviceTicks = 0;
                    if (_comNoDeviceWarned)
                    {
                        _comNoDeviceWarned = false;
                        _warning = null;
                        AppendLog("✓ Cihaz göründü: model='" + model + "' serial='" + serial + "'");
                        UpdateStatusLabel();
                    }
                    WarnIfMultipleDevices(serial);
                }
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

            Program.LogLine("[COM OnCallerID fire] phone='" + phone + "' line='" + SafeProp(e, "line") +
                "' dt='" + SafeProp(e, "dateTime") + "' deviceSerial='" + SafeProp(e, "deviceSerial") + "'");

            // COM event'i UI thread'inde gelir — ortak ingest yoluna ver.
            HandleIncomingCaller(phone, "com");
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
                    else if (key == "deviceMode")
                        _deviceMode = (val == "cid") ? "cid" : "com";
                    else if (key == "autoSwitched")
                        _autoSwitchedToCom = val == "1";
                }
            }
            catch { /* best-effort */ }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigPath,
                    $"deviceToken={_deviceToken}\r\napiBase={_apiBase}\r\ndeviceMode={_deviceMode}\r\n" +
                    $"autoSwitched={(_autoSwitchedToCom ? "1" : "0")}\r\n");
            }
            catch { /* best-effort */ }
        }
    }
}
