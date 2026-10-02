// ConnectTogether.App/ViewModels/RoomViewModels.cs

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    public enum PingQuality
    {
        None,
        Good,
        Fair,
        Poor,
    }

    /// <summary>Пороги качества связи из стайлгайда: до 60 мс — отлично, 60–120 — нормально, больше — плохо.</summary>
    public static class Ping
    {
        public static PingQuality Quality(int? ms) => ms switch
        {
            null => PingQuality.None,
            < 60 => PingQuality.Good,
            <= 120 => PingQuality.Fair,
            _ => PingQuality.Poor,
        };

        public static string Label(PingQuality q) => q switch
        {
            PingQuality.Good => "Отлично",
            PingQuality.Fair => "Нормально",
            PingQuality.Poor => "Плохо",
            _ => "",
        };

        public static int Level(PingQuality q) => q switch
        {
            PingQuality.Good => 3,
            PingQuality.Fair => 2,
            PingQuality.Poor => 1,
            _ => 0,
        };
    }

    /// <summary>Цвета аватаров по порядку входа в комнату.</summary>
    public static class Avatars
    {
        private static readonly Brush[] Palette =
        {
            Freeze("#A3A9FF"), Freeze("#7FD6B0"), Freeze("#F2C77A"), Freeze("#9CC8F5"), Freeze("#C9B6F2"),
        };

        public static Brush ForIndex(int index) => Palette[index % Palette.Length];

        /// <summary>Цвет по id участника: у человека один цвет во всех списках и после выхода других.</summary>
        public static Brush For(string playerId) =>
            ForIndex(int.TryParse(playerId, out int id) ? id : playerId.Aggregate(0, (h, c) => (h * 31 + c) & int.MaxValue));

        public static string Initial(string name) => name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";

        private static Brush Freeze(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Строка игрока: имя, статус, пинг.</summary>
    public sealed class PlayerItem
    {
        public PlayerItem(RoomPlayer player, DateTime now)
        {
            Name = player.IsYou ? $"{player.Name} (вы)" : player.Name;
            Initial = Avatars.Initial(player.Name);
            Avatar = Avatars.For(player.Id);
            Status = player.Status;
            Quality = player.Status == PlayerStatus.Left ? PingQuality.None : Ping.Quality(player.PingMs);
            QualityLevel = Ping.Level(Quality);
            PingText = player.Status switch
            {
                PlayerStatus.Left => "",
                PlayerStatus.Host => "—",
                _ => player.PingMs is int ms ? $"{ms} мс" : "—",
            };
            QualityText = player.Status == PlayerStatus.Left && player.LeftAt is DateTime left
                ? $"{Math.Max(1, (int)(now - left).TotalMinutes)} мин назад"
                : Ping.Label(Quality);
        }

        public string Name { get; }
        public string Initial { get; }
        public Brush Avatar { get; }
        public PlayerStatus Status { get; }
        public bool IsLeft => Status == PlayerStatus.Left;
        public PingQuality Quality { get; }
        public int QualityLevel { get; }
        public string PingText { get; }
        public string QualityText { get; }

        public string StatusText => Status switch
        {
            PlayerStatus.Host => "хост",
            PlayerStatus.InGame => "в игре",
            PlayerStatus.InRoom => "в комнате",
            PlayerStatus.Joining => "подключается",
            _ => "не в комнате",
        };

        public string StatusIcon => Status switch
        {
            PlayerStatus.Host => "server",
            PlayerStatus.InGame => "play",
            PlayerStatus.InRoom => "users",
            PlayerStatus.Joining => "hourglass",
            _ => "logout",
        };
    }

    public sealed record ChatItem(string Author, string Text, bool IsMine, bool IsSystem);

    /// <summary>Общая часть комнат: чат.</summary>
    public abstract class RoomScreenBase : ObservableObject
    {
        private readonly IRoom _room;
        private string _chatInput = "";

        protected RoomScreenBase(ShellViewModel shell, IRoom room)
        {
            Shell = shell;
            _room = room;
            room.MessageReceived += (_, m) => Chat.Add(new ChatItem(m.Author, m.Text, m.IsMine, m.IsSystem));
            Chat.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChat));
            SendCommand = new RelayCommand(Send, () => !string.IsNullOrWhiteSpace(ChatInput));
        }

        protected ShellViewModel Shell { get; }

        public ObservableCollection<ChatItem> Chat { get; } = new();

        public bool HasChat => Chat.Count > 0;

        public string ChatInput
        {
            get => _chatInput;
            set => Set(ref _chatInput, value);
        }

        public ICommand SendCommand { get; }

        private void Send()
        {
            string text = ChatInput.Trim();
            Chat.Add(new ChatItem("Вы", text, true, false));
            ChatInput = "";
            _ = _room.SendMessageAsync(text);
        }
    }

    /// <summary>Комната хоста (1i, 2b, 3i).</summary>
    public sealed class HostRoomViewModel : RoomScreenBase
    {
        private static readonly TimeSpan ServerCheckInterval = TimeSpan.FromSeconds(5);
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _serverTimer = new() { Interval = ServerCheckInterval };
        private readonly Dictionary<string, PlayerStatus> _knownStatus = new();
        private int _serverMisses;
        private bool _serverDown;
        private int _maxGuests;
        private bool _letInOpen;
        private string _letInAddress = "";
        private string? _letInError;

        /// <param name="monitorServer">false — не следить за сервером игры (предпросмотр экрана без запущенного сервера).</param>
        public HostRoomViewModel(ShellViewModel shell, IHostedRoom room, bool monitorServer = true) : base(shell, room)
        {
            Room = room;
            foreach (var p in room.Players) _knownStatus[p.Id] = p.Status;
            RebuildPlayers(announce: false);

            room.PlayersChanged += (_, _) => RebuildPlayers(announce: true);
            room.AddressChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Address));
                Shell.Toast("Адрес комнаты изменился — отправьте друзьям новый", ToastKind.Warning, "Скопировать", CopyInvite);
            };
            _clockTimer.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
            _clockTimer.Start();
            _serverTimer.Tick += async (_, _) => await CheckServerAsync();
            if (monitorServer) _serverTimer.Start();

            InviteCommand = new RelayCommand(CopyInvite);
            CloseCommand = new AsyncCommand(CloseAsync);
            ToggleLetInCommand = new RelayCommand(() => LetInOpen = !LetInOpen);
            LetInCommand = new AsyncCommand(LetInAsync);
        }

        /// <summary>Открыта панель «Впустить по адресу».</summary>
        public bool LetInOpen
        {
            get => _letInOpen;
            set => Set(ref _letInOpen, value);
        }

        /// <summary>Внешний адрес игрока — он показан у него на экране подключения.</summary>
        public string LetInAddress
        {
            get => _letInAddress;
            set
            {
                if (Set(ref _letInAddress, value)) LetInError = null;
            }
        }

        public string? LetInError
        {
            get => _letInError;
            private set
            {
                if (Set(ref _letInError, value)) OnPropertyChanged(nameof(HasLetInError));
            }
        }

        public bool HasLetInError => LetInError != null;

        public ICommand ToggleLetInCommand { get; }
        public ICommand LetInCommand { get; }

        private async Task LetInAsync()
        {
            if (!RoomAddress.TryParse(LetInAddress, out var host, out var port, out var error))
            {
                LetInError = error;
                return;
            }
            var endPoint = await RoomAddress.ResolveAsync(host, port, CancellationToken.None);
            if (endPoint == null)
            {
                LetInError = $"Имя {host} не найдено.";
                return;
            }

            Room.LetIn(endPoint);
            Trace.WriteLine($"[HostRoom] Letting in {endPoint}");
            Shell.Toast($"Впускаем {endPoint} полторы минуты — пусть друг нажмёт «Подключиться» или «Повторить»", ToastKind.Info);
            LetInAddress = "";
            LetInOpen = false;
        }

        public IHostedRoom Room { get; }
        public GameIntegration Game => Room.Game;
        public string Title => $"Ваша комната · {Game.Name}";
        public string Address => Room.Address;
        public string ElapsedText => RussianText.Duration(DateTime.Now - Room.OpenedAt);
        public string ServerText => $"Работает · порт {Room.Server.Port}";

        public ObservableCollection<PlayerItem> Players { get; } = new();

        public bool IsEmpty => Room.Players.All(p => p.IsYou);

        public string CountLabel
        {
            get
            {
                if (IsEmpty) return "Пока только вы";
                int playing = Room.Players.Count(p => p.Status is PlayerStatus.Host or PlayerStatus.InGame);
                return $"{playing} в игре · до {Room.MaxPlayers}";
            }
        }

        public string SlotsText => $"{Room.Players.Count(p => p.Status != PlayerStatus.Left)} из {Room.MaxPlayers}";

        public ICommand InviteCommand { get; }
        public ICommand CloseCommand { get; }

        public void CopyInvite()
        {
            if (AppServices.CopyToClipboard(ShellViewModel.InviteMessage(Game, Room.Address)))
                Shell.Toast("Сообщение для друга скопировано");
        }

        public async Task CloseAsync()
        {
            _clockTimer.Stop();
            _serverTimer.Stop();
            await Room.CloseAsync();
            Trace.WriteLine($"[HostRoom] Room {Room.Address} closed");
            Shell.LeaveRoom();
        }

        /// <summary>Проверка из экрана «Сервер игры не запущен»: true — сервер снова работает.</summary>
        public async Task<bool> RecheckServerAsync()
        {
            bool running = await Shell.Services.ServerProbe.IsRunningAsync(Room.Server);
            if (running)
            {
                _serverMisses = 0;
                _serverDown = false;
            }
            return running;
        }

        private async Task CheckServerAsync()
        {
            if (_serverDown) return;
            bool running = await Shell.Services.ServerProbe.IsRunningAsync(Room.Server);
            _serverMisses = running ? 0 : _serverMisses + 1;
            if (_serverMisses < 2) return; // Одну неудачную проверку не считаем — сервер мог перезапускаться

            _serverDown = true;
            Trace.WriteLine($"[HostRoom] Game server {Room.Server} stopped responding");
            var state = StateViewModel.ServerDown(Shell, this, (int)(ServerCheckInterval.TotalSeconds * _serverMisses));
            if (ReferenceEquals(Shell.Current, this))
                Shell.Navigate(state);
            else
                Shell.Toast("Сервер игры не отвечает", ToastKind.Warning, "Подробнее", () => Shell.Navigate(state));
        }

        private void RebuildPlayers(bool announce)
        {
            var now = DateTime.Now;
            Players.Clear();
            foreach (var player in Room.Players) Players.Add(new PlayerItem(player, now));

            if (announce)
            {
                foreach (var p in Room.Players)
                {
                    _knownStatus.TryGetValue(p.Id, out var before);
                    bool isNew = !_knownStatus.ContainsKey(p.Id);
                    if (isNew)
                        Shell.Toast($"Новый игрок в комнате: {p.Name}", ToastKind.Info);
                    else if (before != PlayerStatus.Left && p.Status == PlayerStatus.Left)
                        Shell.Toast($"Игрок отключился: {p.Name}", ToastKind.Warning, "Пригласить", CopyInvite);
                    _knownStatus[p.Id] = p.Status;
                }
            }

            int guests = Room.Players.Count(p => !p.IsYou && p.Status != PlayerStatus.Left);
            if (guests > _maxGuests)
            {
                _maxGuests = guests;
                Shell.Services.Settings.AddRecentRoom(new RecentRoom
                {
                    IntegrationId = Game.Id, IsHost = true, Address = Room.Address, HostName = Shell.Services.PlayerName,
                    PlayerCount = guests + 1, Time = DateTime.Now,
                });
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(SlotsText));
        }
    }

    public sealed record RosterItem(string Initial, Brush Avatar, string Name, string? Role);

    /// <summary>Комната игрока (1j, 1k, 2c).</summary>
    public sealed class PlayerRoomViewModel : RoomScreenBase
    {
        private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
        private bool _isCopied;

        public PlayerRoomViewModel(ShellViewModel shell, IJoinedRoom room) : base(shell, room)
        {
            Room = room;
            Chat.Add(new ChatItem("", "Вы подключились к комнате", false, true));
            RebuildRoster();

            room.PlayersChanged += (_, _) => RebuildRoster();
            room.PingChanged += (_, _) => OnPingChanged();
            room.ConnectionLost += (_, _) => OnConnectionLost();
            room.LocalEndPointChanged += (_, _) => OnLocalEndPointChanged();
            room.Closed += async (_, _) => await OnRoomClosedAsync();
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                IsCopied = false;
            };

            CopyCommand = new RelayCommand(Copy, () => HasLocalEndPoint);
            LeaveCommand = new AsyncCommand(LeaveAsync);
        }

        public IJoinedRoom Room { get; }
        public GameIntegration Game => Room.Game;
        public string Title => $"Комната {RussianText.Genitive(Room.HostName)} · {Game.Name}";

        public string PingText => Room.PingMs is int ms ? $"{ms} мс" : "—";
        public PingQuality Quality => Ping.Quality(Room.PingMs);
        public int QualityLevel => Ping.Level(Quality);
        public string QualityText => Ping.Label(Quality);

        public string? ConsoleKey => Game.Join.ConsoleKey;
        public bool HasConsoleKey => ConsoleKey != null;
        public IReadOnlyList<string> MenuPath => Game.Join.MenuPath;
        public bool HasMenuPath => MenuPath.Count > 0;
        /// <summary>Хост открывает порт сервера сразу после входа; до этого показывать нечего.</summary>
        public bool HasLocalEndPoint => Room.LocalEndPoint != null;

        public string CommandText => Room.LocalEndPoint is { } endPoint ? Game.Join.Format(endPoint) : "Ждём порт игры от хоста…";

        /// <summary>Голый адрес крупнее (30), команда с адресом — 24, чтобы поместилась.</summary>
        public double CommandFontSize => Game.Join.Template == "{address}" ? 30 : 24;

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

        public string CopyLabel => IsCopied ? "Скопировано" : Game.Join.CopyLabel;
        public string CopyIcon => IsCopied ? "check" : "copy";

        public ObservableCollection<RosterItem> Roster { get; } = new();

        public ICommand CopyCommand { get; }
        public ICommand LeaveCommand { get; }

        public async Task LeaveAsync()
        {
            await Room.LeaveAsync();
            Trace.WriteLine($"[PlayerRoom] Left room {Room.Address}");
            Shell.LeaveRoom();
        }

        private void Copy()
        {
            if (!AppServices.CopyToClipboard(CommandText)) return;
            IsCopied = true;
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        private void OnPingChanged()
        {
            OnPropertyChanged(nameof(PingText));
            OnPropertyChanged(nameof(Quality));
            OnPropertyChanged(nameof(QualityLevel));
            OnPropertyChanged(nameof(QualityText));
        }

        private void OnConnectionLost()
        {
            Trace.WriteLine($"[PlayerRoom] Connection to room {Room.Address} lost");
            Shell.Navigate(new ConnectionLostViewModel(Shell, this));
        }

        private void OnLocalEndPointChanged()
        {
            OnPropertyChanged(nameof(HasLocalEndPoint));
            OnPropertyChanged(nameof(CommandText));
            RelayCommand.Refresh();
        }

        private async Task OnRoomClosedAsync()
        {
            Trace.WriteLine($"[PlayerRoom] Host closed room {Room.Address}");
            Shell.Toast($"{Room.HostName}: комната закрыта", ToastKind.Warning);
            await Room.LeaveAsync();
            Shell.LeaveRoom();
        }

        private void RebuildRoster()
        {
            Roster.Clear();
            foreach (var p in Room.Players.Where(p => p.Status != PlayerStatus.Left))
            {
                string? role = p.Status == PlayerStatus.Host ? "хост" : p.IsYou ? "вы" : null;
                Roster.Add(new RosterItem(Avatars.Initial(p.Name), Avatars.For(p.Id), p.Name, role));
            }
        }
    }
}
