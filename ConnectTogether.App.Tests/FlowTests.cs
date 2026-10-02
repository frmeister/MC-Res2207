// ConnectTogether.App.Tests/FlowTests.cs

using System.IO;
using System.Net;
using ConnectTogether.App.Integrations;
using ConnectTogether.App.Models;
using ConnectTogether.App.Services;
using ConnectTogether.App.ViewModels;

namespace ConnectTogether.App.Tests
{
    /// <summary>
    /// Переходы между экранами по схеме из макета (CT Flow): путь хоста, путь игрока, ошибки.
    /// Сервисы подменены: проверка сервера и комнаты отвечают сразу, сеть не нужна.
    /// </summary>
    public class FlowTests : IDisposable
    {
        private const string HostAddress = "203.0.113.5:47312";

        private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"ct-flow-{Guid.NewGuid():N}.json");
        private readonly FakeProbe _probe = new();
        private readonly FakeRooms _rooms = new();
        private readonly ShellViewModel _shell;

        public FlowTests()
        {
            var settings = new SettingsStore(_settingsPath);
            settings.Update(s => s.PlayerName = "Лёша");
            _shell = new ShellViewModel(new AppServices
            {
                Settings = settings,
                Integrations = IntegrationCatalog.CreateBuiltIn(),
                ServerProbe = _probe,
                Rooms = _rooms,
                Toasts = new ToastService(),
                Theme = new ThemeService(),
            });
        }

        public void Dispose() => File.Delete(_settingsPath);

        [Fact]
        public void Start_WithoutName_OpensOnboarding()
        {
            _shell.Services.Settings.Update(s => s.PlayerName = "");
            _shell.GoHome();

            Assert.IsType<OnboardingViewModel>(_shell.Current);
            Assert.False(_shell.ShowNav);
        }

        [Fact]
        public void HostPath_GameServerFound_CreatesRoomAndReturnsHomeAfterClosing()
        {
            _shell.StartCreateRoom("rust");
            var choose = Assert.IsType<ChooseGameViewModel>(_shell.Current);
            Assert.True(choose.Games.Single(g => g.Game.Id == "rust").IsSelected);

            choose.NextCommand.Execute(null);
            var check = Assert.IsType<ServerCheckViewModel>(_shell.Current);
            Assert.True(check.IsFound);
            Assert.Equal("28015", check.FoundPort);

            check.CreateRoomCommand.Execute(null);
            var invite = Assert.IsType<InviteViewModel>(_shell.Current);
            Assert.Equal(HostAddress, invite.Address);
            Assert.Contains($"Адрес комнаты: {HostAddress}", invite.Message);
            Assert.Equal("192.168.1.5:47312", invite.LanAddress);
            Assert.False(invite.IsWarning);

            invite.GoToRoomCommand.Execute(null);
            var room = Assert.IsType<HostRoomViewModel>(_shell.Current);
            Assert.True(room.IsEmpty);
            Assert.Equal("Пока только вы", room.CountLabel);

            // Вкладка «Главная» из настроек возвращает в открытую комнату
            _shell.ShowSettings();
            _shell.GoHome();
            Assert.Same(room, _shell.Current);

            room.CloseCommand.Execute(null);
            Assert.IsType<HomeViewModel>(_shell.Current);
            Assert.Null(_shell.ActiveRoom);
            var recent = Assert.Single(_shell.Services.Settings.Current.RecentRooms);
            Assert.True(recent.IsHost);
            Assert.Equal("rust", recent.IntegrationId);
        }

        [Fact]
        public void HostPath_NoPublicAddress_WarnsThatOnlyLanFriendsCanJoin()
        {
            _rooms.PublicAddress = null;
            _shell.StartCreateRoom("rust");
            ((ChooseGameViewModel)_shell.Current!).NextCommand.Execute(null);
            ((ServerCheckViewModel)_shell.Current!).CreateRoomCommand.Execute(null);

            var invite = Assert.IsType<InviteViewModel>(_shell.Current);
            Assert.True(invite.IsWarning);
            Assert.Contains("локальной сети", invite.NetworkHint);
        }

        [Fact]
        public void HostPath_GameServerMissing_ShowsStartSteps()
        {
            _probe.Found = false;
            _shell.StartCreateRoom("rust");
            ((ChooseGameViewModel)_shell.Current!).NextCommand.Execute(null);

            var check = Assert.IsType<ServerCheckViewModel>(_shell.Current);
            Assert.False(check.IsFound);
            Assert.Equal(3, check.Steps.Count);
            Assert.Contains("RustDedicated.exe", check.Steps[0].Text);
        }

        [Fact]
        public void HostPath_PortBusy_ShowsPortState()
        {
            _rooms.PortBusy = true;
            _shell.StartCreateRoom("rust");
            ((ChooseGameViewModel)_shell.Current!).NextCommand.Execute(null);
            ((ServerCheckViewModel)_shell.Current!).CreateRoomCommand.Execute(null);

            var state = Assert.IsType<StateViewModel>(_shell.Current);
            Assert.Equal("Порт занят другой программой", state.Title);
        }

        [Fact]
        public void Join_BadAddress_ShowsErrorAndStays()
        {
            _shell.StartJoin();
            var join = Assert.IsType<JoinViewModel>(_shell.Current);

            join.ConnectCommand.Execute(null);
            Assert.Same(join, _shell.Current);
            Assert.StartsWith("Введите адрес", join.Error);

            join.Address = "203.0.113.5:99999";
            Assert.Null(join.Error); // Правка адреса убирает старую ошибку
            join.ConnectCommand.Execute(null);
            Assert.StartsWith("Порт", join.Error);
        }

        [Fact]
        public void PlayerPath_Connects_ThenLeavesToHome()
        {
            _shell.StartJoin(HostAddress);
            ((JoinViewModel)_shell.Current!).ConnectCommand.Execute(null);

            var room = Assert.IsType<PlayerRoomViewModel>(_shell.Current);
            Assert.Equal("Комната Марины · Minecraft", room.Title);
            Assert.Equal("127.0.0.1:58703", room.CommandText);
            Assert.Equal("42 мс", room.PingText);
            Assert.Same(room, _shell.ActiveRoom);
            Assert.Equal(_rooms.LastAddress, HostAddress);

            room.LeaveCommand.Execute(null);
            Assert.IsType<HomeViewModel>(_shell.Current);
            var recent = Assert.Single(_shell.Services.Settings.Current.RecentRooms);
            Assert.False(recent.IsHost);
            Assert.Equal(HostAddress, recent.Address);
            Assert.Equal("Марина", recent.HostName);
        }

        [Fact]
        public void PlayerPath_AddressWithoutPort_UsesDefaultPort()
        {
            _shell.StartJoin("203.0.113.5");
            ((JoinViewModel)_shell.Current!).ConnectCommand.Execute(null);

            Assert.IsType<PlayerRoomViewModel>(_shell.Current);
            Assert.Equal("203.0.113.5:47312", _rooms.LastAddress);
        }

        [Theory]
        [InlineData(JoinFailure.HostNotResponding, "Друг не отвечает")]
        [InlineData(JoinFailure.NoInternet, "Нет доступа к интернету или UDP заблокирован")]
        public void PlayerPath_NetworkFailure_ShowsStateAndRetryReconnects(JoinFailure failure, string title)
        {
            _rooms.Failure = failure;
            _shell.StartJoin(HostAddress);
            ((JoinViewModel)_shell.Current!).ConnectCommand.Execute(null);

            var state = Assert.IsType<StateViewModel>(_shell.Current);
            Assert.Equal(title, state.Title);

            _rooms.Failure = null;
            state.PrimaryCommand!.Execute(null);
            Assert.IsType<PlayerRoomViewModel>(_shell.Current);
        }

        [Fact]
        public void HostRoom_LetIn_ValidatesAddressAndAsksRoom()
        {
            _shell.StartCreateRoom("rust");
            ((ChooseGameViewModel)_shell.Current!).NextCommand.Execute(null);
            ((ServerCheckViewModel)_shell.Current!).CreateRoomCommand.Execute(null);
            ((InviteViewModel)_shell.Current!).GoToRoomCommand.Execute(null);
            var room = Assert.IsType<HostRoomViewModel>(_shell.Current);
            var hosted = (FakeHostedRoom)room.Room;

            room.ToggleLetInCommand.Execute(null);
            Assert.True(room.LetInOpen);

            room.LetInAddress = "188.186.82";
            room.LetInCommand.Execute(null);
            Assert.True(room.HasLetInError);
            Assert.Empty(hosted.LetInRequests);

            room.LetInAddress = "Мой адрес: 188.186.82.227:59333";
            room.LetInCommand.Execute(null);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("188.186.82.227"), 59333), Assert.Single(hosted.LetInRequests));
            Assert.False(room.LetInOpen);
        }

        [Fact]
        public void PlayerPath_NoAnswer_ShowsOwnAddressForHost()
        {
            _rooms.Failure = JoinFailure.HostNotResponding;
            _shell.StartJoin(HostAddress);
            ((JoinViewModel)_shell.Current!).ConnectCommand.Execute(null);

            var state = Assert.IsType<StateViewModel>(_shell.Current);
            Assert.Equal("188.186.82.227:47312", state.MyAddress);
            Assert.NotNull(state.CopyMyAddressCommand);
            Assert.Contains(state.Options, o => o.Title == "Впустить по адресу");
        }

        [Fact]
        public void PlayerPath_RoomFull_ReturnsToJoinWithExplanation()
        {
            _rooms.Failure = JoinFailure.RoomFull;
            _shell.StartJoin(HostAddress);
            ((JoinViewModel)_shell.Current!).ConnectCommand.Execute(null);

            var join = Assert.IsType<JoinViewModel>(_shell.Current);
            Assert.Equal(HostAddress, join.Address);
            Assert.StartsWith("Комната заполнена", join.Error);
        }

        private sealed class FakeProbe : IGameServerProbe
        {
            public bool Found { get; set; } = true;

            public Task<ServerProbeResult> ProbeAsync(GameIntegration game, CancellationToken ct = default) =>
                Task.FromResult(new ServerProbeResult(Found, Found ? game.Endpoints[0] : null, new[] { "fake" }));

            public Task<bool> IsRunningAsync(ServerEndpoint endpoint, CancellationToken ct = default) => Task.FromResult(Found);
        }

        private sealed class FakeRooms : IRoomService
        {
            public JoinFailure? Failure { get; set; }
            public bool PortBusy { get; set; }
            public IPAddress? PublicAddress { get; set; } = IPAddress.Parse("203.0.113.5");
            public string? LastAddress { get; private set; }

            public Task<IHostedRoom> CreateRoomAsync(GameIntegration game, ServerEndpoint server, string hostName, CancellationToken ct = default)
            {
                if (PortBusy) throw new System.Net.Sockets.SocketException(10048); // WSAEADDRINUSE
                return Task.FromResult<IHostedRoom>(new FakeHostedRoom(game, server, hostName, PublicAddress));
            }

            public Task<IJoinedRoom> JoinRoomAsync(string address, string playerName, IProgress<JoinProgress>? progress, CancellationToken ct = default)
            {
                LastAddress = address;
                if (Failure is JoinFailure failure) throw new RoomJoinException(failure, new[] { "fake" }, "188.186.82.227:47312");
                return Task.FromResult<IJoinedRoom>(new FakeJoinedRoom(address, playerName));
            }
        }

        private sealed class FakeHostedRoom : IHostedRoom
        {
            public FakeHostedRoom(GameIntegration game, ServerEndpoint server, string hostName, IPAddress? publicAddress)
            {
                Game = game;
                Server = server;
                PublicAddress = publicAddress;
                Players = new[] { new RoomPlayer { Id = "0", Name = hostName, Status = PlayerStatus.Host, IsYou = true } };
            }

            public string Address => PublicAddress != null ? $"{PublicAddress}:{LocalPort}" : LanAddress!;
            public GameIntegration Game { get; }
            public IReadOnlyList<RoomPlayer> Players { get; }
            public ServerEndpoint Server { get; }
            public DateTime OpenedAt { get; } = DateTime.Now;
            public int MaxPlayers => 10;
            public int LocalPort => 47312;
            public IPAddress? PublicAddress { get; }
            public string? LanAddress => "192.168.1.5:47312";
            public bool? IsSymmetricNat => false;
            public List<IPEndPoint> LetInRequests { get; } = new();
            public void LetIn(IPEndPoint player) => LetInRequests.Add(player);
            public event EventHandler? PlayersChanged { add { } remove { } }
            public event EventHandler<ChatMessage>? MessageReceived { add { } remove { } }
            public event EventHandler? AddressChanged { add { } remove { } }
            public Task SendMessageAsync(string text) => Task.CompletedTask;
            public Task CloseAsync() => Task.CompletedTask;
        }

        private sealed class FakeJoinedRoom : IJoinedRoom
        {
            public FakeJoinedRoom(string address, string playerName)
            {
                Address = address;
                Players = new[]
                {
                    new RoomPlayer { Id = "0", Name = HostName, Status = PlayerStatus.Host },
                    new RoomPlayer { Id = "1", Name = playerName, Status = PlayerStatus.InRoom, IsYou = true },
                };
            }

            public string Address { get; }
            public GameIntegration Game => IntegrationCatalog.Minecraft;
            public string HostName => "Марина";
            public IPEndPoint? LocalEndPoint => new(IPAddress.Loopback, 58703);
            public int? PingMs => 42;
            public IReadOnlyList<RoomPlayer> Players { get; }
            public event EventHandler? PlayersChanged { add { } remove { } }
            public event EventHandler<ChatMessage>? MessageReceived { add { } remove { } }
            public event EventHandler? PingChanged { add { } remove { } }
            public event EventHandler? LocalEndPointChanged { add { } remove { } }
            public event EventHandler? ConnectionLost { add { } remove { } }
            public event EventHandler? Closed { add { } remove { } }
            public Task SendMessageAsync(string text) => Task.CompletedTask;
            public Task<bool> ReconnectAsync(CancellationToken ct = default) => Task.FromResult(true);
            public Task LeaveAsync() => Task.CompletedTask;
        }
    }
}
