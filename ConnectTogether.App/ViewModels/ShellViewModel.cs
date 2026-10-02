// ConnectTogether.App/ViewModels/ShellViewModel.cs

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    public enum NavTab
    {
        None,
        Home,
        Integrations,
        Settings,
    }

    /// <summary>Экран, которому нужно знать о показе и уходе (таймеры, отмена операций).</summary>
    public interface INavigationAware
    {
        void OnNavigatedTo();
        void OnNavigatedFrom();
    }

    /// <summary>
    /// Окно приложения: текущий экран, вкладки в заголовке, тосты и переходы по схеме из макета (CT Flow).
    /// </summary>
    public sealed class ShellViewModel : ObservableObject
    {
        private object? _current;
        private NavTab _tab = NavTab.Home;
        private bool _showNav = true;

        public ShellViewModel(AppServices services)
        {
            Services = services;
            HomeCommand = new RelayCommand(GoHome);
            IntegrationsCommand = new RelayCommand(ShowIntegrations);
            SettingsCommand = new RelayCommand(ShowSettings);
            CreateRoomShortcut = new RelayCommand(() => StartCreateRoom(), () => ShowNav && ActiveRoom == null);
            JoinShortcut = new RelayCommand(() => StartJoin(), () => ShowNav && ActiveRoom == null);
        }

        public AppServices Services { get; }

        public object? Current
        {
            get => _current;
            private set => Set(ref _current, value);
        }

        public NavTab Tab
        {
            get => _tab;
            private set
            {
                if (!Set(ref _tab, value)) return;
                OnPropertyChanged(nameof(IsHomeTab));
                OnPropertyChanged(nameof(IsIntegrationsTab));
                OnPropertyChanged(nameof(IsSettingsTab));
            }
        }

        public bool IsHomeTab => Tab == NavTab.Home;
        public bool IsIntegrationsTab => Tab == NavTab.Integrations;
        public bool IsSettingsTab => Tab == NavTab.Settings;

        /// <summary>Навигация в заголовке скрыта на первом запуске.</summary>
        public bool ShowNav
        {
            get => _showNav;
            private set => Set(ref _showNav, value);
        }

        /// <summary>Открытая комната (хоста или игрока): вкладка «Главная» возвращает в неё, а не на главный экран.</summary>
        public object? ActiveRoom { get; set; }

        public ObservableCollection<Toast> Toasts => Services.Toasts.Items;

        public ICommand HomeCommand { get; }
        public ICommand IntegrationsCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand CreateRoomShortcut { get; }
        public ICommand JoinShortcut { get; }

        /// <summary>Первый экран: проверка порта приложения, затем первый запуск или главный экран.</summary>
        public void Start()
        {
            int port = Services.Settings.Current.AppPort;
            if (PortInspector.IsUdpPortInUse(port))
            {
                Trace.WriteLine($"[Shell] App port {port} is busy");
                Navigate(StateViewModel.PortBusy(this, port, PortInspector.DescribeUdpOwner(port)));
                return;
            }
            GoHome();
        }

        public void Navigate(object screen, NavTab tab = NavTab.Home, bool showNav = true)
        {
            if (ReferenceEquals(screen, Current)) return;
            (Current as INavigationAware)?.OnNavigatedFrom();
            Tab = tab;
            ShowNav = showNav;
            Current = screen;
            (screen as INavigationAware)?.OnNavigatedTo();
            RelayCommand.Refresh();
        }

        /// <summary>Вкладка «Главная»: открытая комната, первый запуск (если имени ещё нет) или главный экран.</summary>
        public void GoHome()
        {
            if (ActiveRoom != null)
                Navigate(ActiveRoom);
            else if (string.IsNullOrWhiteSpace(Services.PlayerName))
                Navigate(new OnboardingViewModel(this), NavTab.None, showNav: false);
            else
                Navigate(new HomeViewModel(this));
        }

        public void StartCreateRoom(string? integrationId = null) => Navigate(new ChooseGameViewModel(this, integrationId));

        public void StartJoin(string? address = null, string? error = null) => Navigate(new JoinViewModel(this, address, error));

        public void ShowIntegrations() => Navigate(new IntegrationsViewModel(this), NavTab.Integrations);

        public void ShowSettings() => Navigate(new SettingsViewModel(this), NavTab.Settings);

        /// <summary>Открывает комнату и делает её активной.</summary>
        public void EnterRoom(object roomScreen)
        {
            ActiveRoom = roomScreen;
            Navigate(roomScreen);
        }

        /// <summary>Комната закрыта или покинута: возвращаемся на главный экран.</summary>
        public void LeaveRoom()
        {
            ActiveRoom = null;
            GoHome();
        }

        public void Toast(string text, ToastKind kind = ToastKind.Success, string? actionLabel = null, Action? action = null) =>
            Services.Toasts.Show(text, kind, actionLabel, action);

        /// <summary>Сообщение для друга с адресом комнаты.</summary>
        public static string InviteMessage(GameIntegration game, string address) =>
            $"Привет! Заходи ко мне в {game.Name} через ConnectTogether. Адрес комнаты: {address}. Приложение: {AppLinks.DownloadPage}";

        /// <summary>
        /// Приложение закрывается: хост закрывает комнату (игроки сразу узнают об этом), игрок выходит из неё.
        /// Ждём недолго и не в потоке интерфейса — закрытию окна это не должно мешать.
        /// </summary>
        public void ShutdownRooms()
        {
            Func<Task>? close = ActiveRoom switch
            {
                HostRoomViewModel host => () => host.Room.CloseAsync(),
                PlayerRoomViewModel player => () => player.Room.LeaveAsync(),
                _ => null,
            };
            if (close == null) return;
            try
            {
                Task.Run(close).Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex)
            {
                Trace.WriteLine($"[Shell] Closing room on exit failed: {ex.InnerException?.Message}");
            }
        }
    }
}
