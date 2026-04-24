using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ProTakipCallerBridgeCom
{
    internal static class Program
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProTakipCallerBridgeCom");
        private static readonly string LogPath = Path.Combine(ConfigDir, "bridge.log");

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
