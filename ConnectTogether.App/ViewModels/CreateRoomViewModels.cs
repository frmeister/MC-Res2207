// ConnectTogether.App/ViewModels/CreateRoomViewModels.cs

using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    /// <summary>Создание комнаты, шаг 1 (1c): выбор игры.</summary>
    public sealed class ChooseGameViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;
        private bool _isChecking;

        public ChooseGameViewModel(ShellViewModel shell, string? integrationId)
        {
            _shell = shell;
            var services = shell.Services;
            string? preselect = integrationId ?? services.Settings.Current.LastIntegrationId ?? services.Integrations.All.FirstOrDefault()?.Id;
            Games = services.Integrations.All.Select(g => new GameOption(g) { IsSelected = g.Id == preselect }).ToList();

            NextCommand = new AsyncCommand(NextAsync, () => Games.Any(g => g.IsSelected));
            BackCommand = new RelayCommand(shell.GoHome);
            OpenIntegrationsCommand = new RelayCommand(shell.ShowIntegrations);
        }

        public IReadOnlyList<GameOption> Games { get; }

        public bool IsChecking
        {
            get => _isChecking;
            private set
            {
                if (Set(ref _isChecking, value)) OnPropertyChanged(nameof(NextLabel));
            }
        }

        public string NextLabel => IsChecking ? "Проверяем…" : "Дальше";

        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand OpenIntegrationsCommand { get; }

        private async Task NextAsync()
        {
            var game = Games.First(g => g.IsSelected).Game;
            _shell.Services.Settings.Update(s => s.LastIntegrationId = game.Id);

            IsChecking = true;
            try
            {
                var result = await _shell.Services.ServerProbe.ProbeAsync(game);
                _shell.Navigate(new ServerCheckViewModel(_shell, game, result));
            }
            finally
            {
                IsChecking = false;
            }
        }
    }

    public sealed class GameOption : ObservableObject
    {
        private bool _isSelected;

        public GameOption(GameIntegration game) => Game = game;

        public GameIntegration Game { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }
    }

    /// <summary>Шаг 2 (1d, 1e): сервер найден или не запущен.</summary>
    public sealed class ServerCheckViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;
        private ServerProbeResult _result;
        private bool _isChecking;
        private bool _detailsOpen;

        public ServerCheckViewModel(ShellViewModel shell, GameIntegration game, ServerProbeResult result)
        {
            _shell = shell;
            Game = game;
            _result = result;
            Steps = game.StartServerSteps.Select((t, i) => new NumberedItem(i + 1, t)).ToList();

            BackCommand = new RelayCommand(() => shell.StartCreateRoom(game.Id));
            RetryCommand = new AsyncCommand(RetryAsync);
            CreateRoomCommand = new AsyncCommand(CreateRoomAsync);
            InstallHelpCommand = new RelayCommand(() => AppLinks.Open(game.InstallHelpUrl));
        }

        public GameIntegration Game { get; }

        public bool IsFound => _result.Found;

        public string FoundTitle => $"Сервер {Game.Name} найден на этом компьютере ✓";
        public string FoundPrefix => $"{Game.ServerTitle} отвечает на порту ";
        public string FoundPort => _result.Endpoint?.Port.ToString() ?? "";

        public string ErrorDescription => $"ConnectTogether не нашёл сервер {Game.Name} на этом компьютере.";

        public IReadOnlyList<NumberedItem> Steps { get; }

        public string Details => string.Join(Environment.NewLine, _result.Details);

        public bool DetailsOpen
        {
            get => _detailsOpen;
            set => Set(ref _detailsOpen, value);
        }

        public bool IsChecking
        {
            get => _isChecking;
            private set
            {
                if (Set(ref _isChecking, value)) OnPropertyChanged(nameof(RetryLabel));
            }
        }

        public string RetryLabel => IsChecking ? "Проверяем…" : "Проверить снова";

        public ICommand BackCommand { get; }
        public ICommand RetryCommand { get; }
        public ICommand CreateRoomCommand { get; }
        public ICommand InstallHelpCommand { get; }

        private async Task RetryAsync()
        {
            IsChecking = true;
            try
            {
                // Короткая пауза, чтобы было видно, что проверка действительно прошла заново
                var minimum = Task.Delay(600);
                var result = await _shell.Services.ServerProbe.ProbeAsync(Game);
                await minimum;
                _result = result;
                OnPropertyChanged(string.Empty);
            }
            finally
            {
                IsChecking = false;
            }
        }

        private async Task CreateRoomAsync()
        {
            var server = _result.Endpoint ?? Game.Endpoints[0];
            IsCreating = true;
            try
            {
                var room = await _shell.Services.Rooms.CreateRoomAsync(Game, server, _shell.Services.PlayerName);
                _shell.Services.Settings.AddRecentRoom(new RecentRoom
                {
                    IntegrationId = Game.Id, IsHost = true, Address = room.Address, HostName = _shell.Services.PlayerName, Time = DateTime.Now,
                });
                _shell.Navigate(new InviteViewModel(_shell, room));
            }
            catch (SocketException ex)
            {
                // Порт комнаты занят — например, открыта вторая копия ConnectTogether
                int port = _shell.Services.Settings.Current.AppPort;
                Trace.WriteLine($"[ServerCheck] Cannot open room on UDP {port}: {ex.Message}");
                _shell.Navigate(StateViewModel.PortBusy(_shell, port, PortInspector.DescribeUdpOwner(port)));
            }
            finally
            {
                IsCreating = false;
            }
        }

        private bool _isCreating;

        /// <summary>Комната открывается: занимаем порт и узнаём внешний адрес (до нескольких секунд).</summary>
        public bool IsCreating
        {
            get => _isCreating;
            private set
            {
                if (Set(ref _isCreating, value)) OnPropertyChanged(nameof(CreateLabel));
            }
        }

        public string CreateLabel => IsCreating ? "Создаём комнату…" : "Создать комнату";
    }

    public sealed record NumberedItem(int Number, string Text);

    /// <summary>Шаг 3 (1f): адрес комнаты для друзей.</summary>
    public sealed class InviteViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;
        private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
        private bool _isCopied;

        public InviteViewModel(ShellViewModel shell, IHostedRoom room)
        {
            _shell = shell;
            Room = room;
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                IsCopied = false;
            };
            room.AddressChanged += (_, _) => OnPropertyChanged(string.Empty);

            CopyAddressCommand = new RelayCommand(CopyAddress);
            CopyMessageCommand = new RelayCommand(CopyMessage);
            GoToRoomCommand = new RelayCommand(() => shell.EnterRoom(new HostRoomViewModel(shell, room)));
        }

        public IHostedRoom Room { get; }
        public string Address => Room.Address;
        public string Message => ShellViewModel.InviteMessage(Room.Game, Room.Address);
        public string QuotedMessage => $"«{Message}»";

        /// <summary>Адрес для друзей в той же сети, если он отличается от основного.</summary>
        public string? LanAddress => Room.LanAddress != null && Room.LanAddress != Room.Address ? Room.LanAddress : null;

        /// <summary>Подключение из интернета, скорее всего, не пройдёт без действий хоста.</summary>
        public bool IsWarning => Room.PublicAddress == null || Room.IsSymmetricNat == true;

        public string NetworkHint
        {
            get
            {
                int port = Room.LocalPort;
                if (Room.PublicAddress == null)
                    return "Внешний адрес определить не удалось: нет интернета или UDP заблокирован. Подключиться смогут только друзья из вашей локальной сети.";
                string forwarded = $"{Room.PublicAddress}:{port}";
                return Room.IsSymmetricNat == true
                    ? $"Ваш роутер меняет порт для каждого подключения — друзьям из интернета понадобится проброс UDP-порта {port} на роутере. После проброса отправьте им адрес {forwarded}."
                    : "Если друг из интернета не может подключиться, он увидит на экране свой адрес — впустите его по нему в комнате " +
                      $"(«Впустить по адресу»). Или пробросьте на роутере UDP-порт {port} и отправьте адрес {forwarded}.";
            }
        }

        public bool IsCopied
        {
            get => _isCopied;
            private set
            {
                if (!Set(ref _isCopied, value)) return;
                OnPropertyChanged(nameof(CopyLabel));
                OnPropertyChanged(nameof(CopyIcon));
            }
        }

        public string CopyLabel => IsCopied ? "Скопировано" : "Скопировать";
        public string CopyIcon => IsCopied ? "check" : "copy";

        public ICommand CopyAddressCommand { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand GoToRoomCommand { get; }

        private void CopyAddress()
        {
            if (!AppServices.CopyToClipboard(Address)) return;
            IsCopied = true;
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        private void CopyMessage()
        {
            if (AppServices.CopyToClipboard(Message))
                _shell.Toast("Сообщение для друга скопировано");
        }
    }
}
