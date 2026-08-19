using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProTakipCallerBridgeCom
{
    internal static class Program
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProTakipCallerBridgeCom");
        private static readonly string LogPath = Path.Combine(ConfigDir, "bridge.log");

        /// <summary>
        /// cidv5callerid.dll'in COM kayıtlı olup olmadığını kontrol eder;
        /// değilse yönetici olarak regsvr32 çağırır (UAC prompt çıkar).
        /// Kullanıcı OK derse DLL kayıt olur — bir sonraki çalıştırmada
        /// bu adım atlanır. Reddederse uygulama yine de başlar ama form
        /// ActiveX control oluştururken hata verir.
        /// </summary>
        private static void EnsureCidv5Registered()
        {
            using var key = Registry.ClassesRoot.OpenSubKey("CIDv5CallerID.CIDv5");
            if (key != null)
            {
                LogLine("cidv5callerid already registered in HKCR (ProgID present)");
                return;
            }

            // AppContext net45+, bu proje net40 → Application.StartupPath
            var baseDir = Application.StartupPath;
            var dllPath = Path.Combine(baseDir, "cidv5callerid.dll");
            if (!File.Exists(dllPath))
            {
                LogLine("cidv5callerid.dll NOT found at " + dllPath);
                return;
            }

            LogLine("cidv5callerid not registered — attempting regsvr32 via UAC");
            var psi = new ProcessStartInfo
            {
                FileName = "regsvr32",
                Arguments = "/s \"" + dllPath + "\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // runas verb → Windows UAC prompt gösterir.
                Verb = "runas",
            };

            try
            {
                using var p = Process.Start(psi);
                if (p == null)
                {
                    LogLine("regsvr32 Process.Start returned null");
                    return;
                }
                p.WaitForExit(10000);
                LogLine("regsvr32 exit code: " + p.ExitCode);

                // Doğrula
                using var verifyKey = Registry.ClassesRoot.OpenSubKey("CIDv5CallerID.CIDv5");
                LogLine("Post-register check: ProgID present=" + (verifyKey != null));
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // User cancelled UAC — 1223 "Operation was canceled by the user"
                LogLine("UAC cancelled or regsvr32 missing: " + wex.Message);
                MessageBox.Show(
                    "CID v5 sürücüsü kayıt edilemedi. İlk çalıştırmada 'Evet' demen gerekiyor.\n\n" +
                    "Bridge'i kapatıp tekrar aç, UAC penceresinde 'Evet' tıkla. " +
                    "Ya da klasördeki register.bat'a sağ tık → Yönetici olarak çalıştır.",
                    "ProTakip Caller Id",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private const string AutoStartName = "ProTakipCallerBridgeCom";

        /// <summary>
        /// Köprüyü her Windows oturum açılışında başlatacak şekilde kaydeder.
        ///
        /// <para>HKCU\...\Run TEK BAŞINA sahada yetmedi (Güven Halı / Ankara,
        /// 2026-08-18: "bilgisayarı her yeniden başlattığımda CALLER ID bağlı
        /// değil yazıyor" — tek firmada 16 eşleştirme kaydı birikmişti). İki
        /// sebep:</para>
        /// <list type="number">
        ///   <item>Kullanıcı köprüyü <b>Yönetici olarak çalıştır</b> ile
        ///   açıyor (COM kaydı için zaten öyle söylüyoruz). UAC farklı bir
        ///   yönetici hesabına yükseltiyorsa HKCU o hesabın kovanı olur —
        ///   değer oturum açan kullanıcının Run anahtarına hiç yazılmaz,
        ///   açılışta hiçbir şey başlamaz. Aynı sebeple <c>%APPDATA%</c>
        ///   altındaki config de o profile yazılır, token kaybolur ve
        ///   kullanıcı her açılışta yeniden eşleştirmek zorunda kalır.</item>
        ///   <item>Değer doğru yazılsa bile Windows Run girdilerini
        ///   <b>yetkisiz</b> başlatır; yükseltilmiş çalışması gereken COM
        ///   yolu sessiz kalır.</item>
        /// </list>
        ///
        /// <para>Çözüm: zaten yükseltilmişken — kullanıcıya söylenen "bir kez
        /// yönetici olarak çalıştır" anı — ONLOGON + /RL HIGHEST zamanlanmış
        /// görev oluşturuluyor. Run değeri yetkisiz çalıştırmalar için yedek
        /// kalıyor ve görev kurulduğunda siliniyor ki ikinci (yetkisiz) kopya
        /// açılıp COM cihazı için kavga etmesin.</para>
        ///
        /// <para>Aynı düzeltme self-contained köprüde 1.0.1 ile çıkmıştı; asıl
        /// dağıtılan ürün bu (bridge-com) olduğu için buraya da taşındı.</para>
        /// </summary>
        private static void RegisterAutoStart()
        {
            // net40'ta Environment.ProcessPath yok, Application.ExecutablePath
            // exe'nin tam yolunu verir.
            var exePath = Application.ExecutablePath;
            if (string.IsNullOrEmpty(exePath)) return;

            var taskRegistered = false;
            if (IsElevated())
            {
                taskRegistered = TryRegisterLogonTask(exePath);
            }

            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            var quoted = "\"" + exePath + "\"";

            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null) { LogLine("AutoStart: Run key açılamadı"); return; }

            if (taskRegistered)
            {
                if (key.GetValue(AutoStartName) != null)
                {
                    key.DeleteValue(AutoStartName, throwOnMissingValue: false);
                    LogLine("AutoStart: Run değeri silindi (açılışı zamanlanmış görev üstlendi)");
                }
                return;
            }

            var existing = key.GetValue(AutoStartName) as string;
            if (existing == quoted)
            {
                LogLine("AutoStart: kayıt zaten mevcut — " + quoted);
                return;
            }
            key.SetValue(AutoStartName, quoted);
            LogLine("AutoStart: kayıt edildi — " + quoted);
        }

        /// <summary>Süreç yönetici haklarıyla mı çalışıyor?</summary>
        private static bool IsElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ONLOGON zamanlanmış görevini <c>schtasks.exe</c> ile kurar/yeniler.
        /// COM bağımlılığı eklememek için kasıtlı olarak dış süreç çağrılıyor;
        /// schtasks desteklenen her Windows'ta mevcut. <c>/F</c> mevcut görevi
        /// ezer — kullanıcı ZIP'i başka klasöre çıkardığında exe yolu değişir.
        /// </summary>
        private static bool TryRegisterLogonTask(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Create /F /SC ONLOGON /RL HIGHEST /TN \"" + AutoStartName
                              + "\" /TR \"\\\"" + exePath + "\\\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                proc.WaitForExit(15000);
                if (proc.ExitCode == 0)
                {
                    LogLine("AutoStart: zamanlanmış görev kuruldu (ONLOGON, en yüksek yetki)");
                    return true;
                }

                LogLine("AutoStart: zamanlanmış görev kurulamadı (çıkış " + proc.ExitCode + "): "
                        + proc.StandardError.ReadToEnd().Trim());
                return false;
            }
            catch (Exception ex)
            {
                LogLine("AutoStart: zamanlanmış görev kurulamadı (ölümcül değil): " + ex.Message);
                return false;
            }
        }

        internal static void LogLine(string line)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + line + Environment.NewLine);
            }
            catch { /* best-effort */ }
        }

        [STAThread]
        private static void Main()
        {
            // Global error capture — eğer MainForm ctor veya ActiveX init
            // sessizce exception fırlatırsa log'a ve MessageBox'a yansısın,
            // kullanıcı siyah ekranla kalmasın.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogLine("UNHANDLED: " + e.ExceptionObject);
                try
                {
                    MessageBox.Show(
                        "Beklenmeyen hata:\n\n" + e.ExceptionObject + "\n\nLog: " + LogPath,
                        "ProTakip Caller Id — COM",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                LogLine("UI THREAD EXCEPTION: " + e.Exception);
                MessageBox.Show(
                    "Arayüz hatası:\n\n" + e.Exception.Message + "\n\nLog: " + LogPath,
                    "ProTakip Caller Id — COM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            LogLine("=== Program.Main başladı ===");
            LogLine("Exe: " + System.Reflection.Assembly.GetExecutingAssembly().Location);
            LogLine("OS: " + Environment.OSVersion + "  64bit process=" + Environment.Is64BitProcess);
            LogLine("CLR: " + Environment.Version);
            LogLine("Apartment: " + Thread.CurrentThread.GetApartmentState());

            // .NET Framework 4.0 varsayılan TLS 1.0 ile HTTPS'e çıkıyor.
            // api.protakip.com (ve modern tüm sunucular) TLS 1.2+ istiyor →
            // HttpWebRequest handshake sırasında "no-response" atıyor. net40'ta
            // SecurityProtocolType.Tls11 (768) / Tls12 (3072) enum değerleri
            // YOK; int cast ile manuel veriyoruz. Tls + Tls11 + Tls12 fallback.
            try
            {
                const int TLS11 = 768;
                const int TLS12 = 3072;
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)(TLS11 | TLS12) | SecurityProtocolType.Tls;
                ServicePointManager.Expect100Continue = false;
                LogLine("TLS 1.2 enabled (SecurityProtocol=" + (int)ServicePointManager.SecurityProtocol + ")");
            }
            catch (Exception ex)
            {
                LogLine("TLS config failed: " + ex.Message);
            }

            try
            {
                EnsureCidv5Registered();
            }
            catch (Exception ex)
            {
                LogLine("COM register attempt failed (non-fatal): " + ex.Message);
            }

            try
            {
                RegisterAutoStart();
            }
            catch (Exception ex)
            {
                LogLine("Auto-start register failed (non-fatal): " + ex.Message);
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                LogLine("MainForm oluşturuluyor...");
                var form = new MainForm();
                LogLine("MainForm oluşturuldu, Application.Run başlatılıyor");
                Application.Run(form);
                LogLine("=== Program.Main normal çıkış ===");
            }
            catch (Exception ex)
            {
                LogLine("FATAL Main(): " + ex);
                MessageBox.Show(
                    "Başlatma hatası:\n\n" + ex.Message + "\n\nLog: " + LogPath,
                    "ProTakip Caller Id — COM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
