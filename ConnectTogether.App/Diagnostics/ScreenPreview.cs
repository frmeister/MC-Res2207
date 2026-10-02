// ConnectTogether.App/Diagnostics/ScreenPreview.cs

using System.IO;
using ConnectTogether.App.Integrations;
using ConnectTogether.App.Models;
using ConnectTogether.App.Services;
using ConnectTogether.App.ViewModels;

namespace ConnectTogether.App.Diagnostics
{
    /// <summary>
    /// Открывает любой экран с данными из макета — чтобы сверять вёрстку с дизайном, не проходя весь путь.
    /// Запуск: ConnectTogether.exe --screen 1i [--theme light]. Номера экранов — как на холсте дизайна.
    /// Настройки берутся из временного файла, настоящие не меняются.
    /// </summary>
    public static class ScreenPreview
    {
        public static readonly string[] Ids =
        {
            "1a", "1b", "1c", "1d", "1e", "1f", "1g", "1h", "1i", "1j", "1k", "1l", "1m",
            "3a", "3d", "3e", "3f", "3g", "3h", "3i", "letin",
        };

        public static ShellViewModel? TryCreateShell(string[] args)
        {
            string? id = DevTools.ArgValue(args, "--screen");
            if (id == null) return null;

            var shell = new ShellViewModel(CreateServices(DevTools.ArgValue(args, "--theme")));
            Open(shell, id);
            return shell;
        }

        public static AppServices CreateServices(string? theme)
        {
            string path = Path.Combine(Path.GetTempPath(), "ConnectTogether-preview", $"settings-{Environment.ProcessId}.json");
            if (File.Exists(path)) File.Delete(path);

            var settings = new SettingsStore(path);
            var now = DateTime.Now;
            settings.Update(s =>
            {
                s.PlayerName = "Лёша";
                s.Theme = theme == "light" ? ThemeMode.Light : ThemeMode.Dark;
                s.RecentRooms = new List<RecentRoom>
                {
                    new() { IntegrationId = "rust", IsHost = true, Address = "203.0.113.5:47312", HostName = "Лёша", PlayerCount = 3, Time = now.Date.AddDays(-1).AddHours(22).AddMinutes(10) },
                    new() { IntegrationId = "minecraft", IsHost = false, Address = "198.51.100.24:47312", HostName = "Марина", Time = now.AddDays(-2) },
                    new() { IntegrationId = "minecraft", IsHost = true, Address = "203.0.113.5:47312", HostName = "Лёша", PlayerCount = 5, Time = now.AddDays(-12) },
                };
            });

            var services = AppServices.CreateDefault(settings);
            services.Theme.Apply(settings.Current.Theme);
            return services;
        }

        public static void Open(ShellViewModel shell, string id)
        {
            var rust = IntegrationCatalog.Rust;
            var rustServer = new ServerEndpoint(EndpointProtocol.Udp, 28015);
            var probeDetails = new[] { "Проверка: UDP 28015 — никто не слушает", "Процесс RustDedicated.exe — не найден", "Интеграция: rust 1.0.3" };

            switch (id)
            {
                case "1a":
                    shell.Navigate(new OnboardingViewModel(shell), NavTab.None, showNav: false);
                    break;
                case "1b":
                    shell.Navigate(new HomeViewModel(shell));
                    break;
                case "3h":
                    shell.Services.Settings.Update(s => s.RecentRooms.Clear());
                    shell.Navigate(new HomeViewModel(shell));
                    break;
                case "1c":
                    shell.Navigate(new ChooseGameViewModel(shell, "rust"));
                    break;
                case "1d":
                    shell.Navigate(new ServerCheckViewModel(shell, rust, new ServerProbeResult(true, rustServer, probeDetails)));
                    break;
                case "1e":
                    shell.Navigate(new ServerCheckViewModel(shell, rust, new ServerProbeResult(false, null, probeDetails)));
                    break;
                case "1f":
                    shell.Navigate(new InviteViewModel(shell, MockupHostRoom(fill: true)));
                    break;
                case "1g":
                    shell.Navigate(new JoinViewModel(shell, "203.0.113.5:4731"));
                    break;
                case "1h":
                    var connecting = new ConnectingViewModel(shell, "203.0.113.5:47312");
                    connecting.ShowMockupState(null, TimeSpan.FromSeconds(12), "188.186.82.227:47312");
                    shell.Navigate(connecting);
                    break;
                case "1i":
                    shell.EnterRoom(MockupHostScreen(shell, fill: true));
                    break;
                case "3i":
                    shell.EnterRoom(MockupHostScreen(shell, fill: false));
                    break;
                case "letin": // Не из макета: комната хоста с открытой панелью «Впустить по адресу»
                    var letIn = MockupHostScreen(shell, fill: false);
                    letIn.LetInOpen = true;
                    letIn.LetInAddress = "188.186.82.227:47312";
                    shell.EnterRoom(letIn);
                    break;
                case "1j":
                    shell.EnterRoom(MockupPlayerScreen(shell, rust));
                    break;
                case "1k":
                    shell.EnterRoom(MockupPlayerScreen(shell, IntegrationCatalog.Minecraft));
                    break;
                case "1l":
                    shell.ShowIntegrations();
                    break;
                case "1m":
                    shell.ShowSettings();
                    break;
                case "3a":
                    shell.Navigate(StateViewModel.NoAnswer(shell, "203.0.113.5:47312", new[]
                    {
                        "Хост 203.0.113.5:47312: нет ответа за 120 с", "Ваш внешний адрес: 188.186.82.227:47312 (обычный NAT)",
                    }, "188.186.82.227:47312"));
                    break;
                case "3d":
                    var host = MockupHostScreen(shell, fill: true);
                    shell.ActiveRoom = host;
                    shell.Navigate(StateViewModel.ServerDown(shell, host, 45));
                    break;
                case "3e":
                    shell.Navigate(StateViewModel.PortBusy(shell, AppSettings.DefaultAppPort, "netgame.exe (PID 4120)"));
                    break;
                case "3f":
                    var player = MockupPlayerScreen(shell, rust);
                    shell.ActiveRoom = player;
                    var lost = new ConnectionLostViewModel(shell, player);
                    lost.ShowMockupState(seconds: 7, attempt: 2);
                    shell.Navigate(lost);
                    break;
                case "3g":
                    shell.Navigate(StateViewModel.NoInternet(shell, "203.0.113.5:47312", new[]
                    {
                        "Сервер определения адреса: нет ответа", "Исходящий UDP: заблокирован", "HTTPS (TCP 443): доступен",
                    }));
                    break;
                default:
                    shell.Start();
                    break;
            }
        }

        private static PreviewHostedRoom MockupHostRoom(bool fill)
        {
            var room = new PreviewHostedRoom(IntegrationCatalog.Rust, new ServerEndpoint(EndpointProtocol.Udp, 28015), "Лёша",
                DateTime.Now.AddMinutes(-72));
            if (fill) room.FillLikeMockup();
            return room;
        }

        private static HostRoomViewModel MockupHostScreen(ShellViewModel shell, bool fill)
        {
            var room = MockupHostRoom(fill);
            var screen = new HostRoomViewModel(shell, room, monitorServer: false);
            if (fill)
            {
                foreach (var m in room.MockupChat()) screen.Chat.Add(new ChatItem(m.Author, m.Text, m.IsMine, m.IsSystem));
            }
            return screen;
        }

        private static PlayerRoomViewModel MockupPlayerScreen(ShellViewModel shell, GameIntegration game)
        {
            var room = new PreviewJoinedRoom(game, "Лёша", "Тёма");
            var screen = new PlayerRoomViewModel(shell, room);
            screen.Chat.Add(new ChatItem("Лёша", "Привет! Адрес в окне слева, просто скопируй", false, false));
            screen.Chat.Add(new ChatItem("Вы", "Ок, захожу", true, false));
            return screen;
        }
    }
}
