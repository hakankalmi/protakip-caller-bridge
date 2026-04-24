using System;
using System.Diagnostics;
using System.IO;
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
