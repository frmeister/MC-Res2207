// ConnectTogether.App/Services/AppLinks.cs

using System.Diagnostics;
using System.IO;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Внешние ссылки приложения в одном месте.
    /// Сайта у проекта пока нет: адрес в сообщении для друга — заглушка из макета, справка ведёт в репозиторий.
    /// </summary>
    public static class AppLinks
    {
        /// <summary>Откуда друг скачивает приложение. TODO: заменить на настоящий адрес, когда появится сайт.</summary>
        public const string DownloadPage = "connecttogether.app";

        public const string Repository = "https://github.com/frmeister/MC-Res2207";
        public const string IntegrationGuide = Repository;
        public const string PortForwardingHelp = Repository;

        public static void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.WriteLine($"[AppLinks] Failed to open {url}: {ex.Message}");
            }
        }

        /// <summary>Открывает папку в Проводнике, создав её при необходимости.</summary>
        public static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Open(path);
        }

        /// <summary>Окно «Разрешить работу с приложением через брандмауэр».</summary>
        public static void OpenFirewallSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps")
                {
                    UseShellExecute = true,
                })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.WriteLine($"[AppLinks] Failed to open firewall settings: {ex.Message}");
            }
        }
    }
}
