// ConnectTogether.App/Services/AppServices.cs

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using ConnectTogether.App.Integrations;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Сервисы приложения. Модели представления получают их через ShellViewModel.
    /// Комнаты работают через Core (<see cref="CoreRoomService"/>); в тестах сервисы подменяются.
    /// </summary>
    public sealed class AppServices
    {
        public required SettingsStore Settings { get; init; }
        public required IntegrationCatalog Integrations { get; init; }
        public required IGameServerProbe ServerProbe { get; init; }
        public required IRoomService Rooms { get; init; }
        public required ToastService Toasts { get; init; }
        public required ThemeService Theme { get; init; }

        public string PlayerName => Settings.Current.PlayerName;

        public static AppServices CreateDefault(SettingsStore? settings = null)
        {
            var integrations = IntegrationCatalog.CreateBuiltIn();
            settings ??= new SettingsStore();
            return new AppServices
            {
                Settings = settings,
                Integrations = integrations,
                ServerProbe = new LocalServerProbe(),
                Rooms = new CoreRoomService(settings, integrations),
                Toasts = new ToastService(),
                Theme = new ThemeService(),
            };
        }

        /// <summary>Копирует текст в буфер обмена; буфер бывает занят другой программой, поэтому несколько попыток.</summary>
        public static bool CopyToClipboard(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(30);
                }
            }
            Trace.WriteLine("[AppServices] Clipboard is busy, copy failed");
            return false;
        }
    }
}
