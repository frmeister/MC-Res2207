// ConnectTogether.App/Diagnostics/PreviewRooms.cs

using System.Net;
using ConnectTogether.App.Models;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.Diagnostics
{
    /// <summary>
    /// Неподвижные комнаты с данными из макета — только для предпросмотра экранов (--screen, --snapshot).
    /// Сети не касаются.
    /// </summary>
    internal sealed class PreviewHostedRoom : IHostedRoom
    {
        private readonly List<RoomPlayer> _players = new();

        public PreviewHostedRoom(GameIntegration game, ServerEndpoint server, string hostName, DateTime openedAt)
        {
            Game = game;
            Server = server;
            OpenedAt = openedAt;
            _players.Add(new RoomPlayer { Id = "0", Name = hostName, Status = PlayerStatus.Host, IsYou = true });
        }

        public string Address => "203.0.113.5:47312";
        public GameIntegration Game { get; }
        public ServerEndpoint Server { get; }
        public DateTime OpenedAt { get; }
        public int MaxPlayers => 10;
        public int LocalPort => AppSettings.DefaultAppPort;
        public IPAddress? PublicAddress => IPAddress.Parse("203.0.113.5");
        public string? LanAddress => "192.168.1.5:47312";
        public bool? IsSymmetricNat => false;
        public IReadOnlyList<RoomPlayer> Players => _players;
        public void LetIn(IPEndPoint player) { }

        public event EventHandler? PlayersChanged { add { } remove { } }
        public event EventHandler<ChatMessage>? MessageReceived { add { } remove { } }
        public event EventHandler? AddressChanged { add { } remove { } }

        /// <summary>Пятеро игроков, как на макете комнаты хоста.</summary>
        public void FillLikeMockup()
        {
            _players.Add(new RoomPlayer { Id = "1", Name = "Марина", Status = PlayerStatus.InGame, PingMs = 34 });
            _players.Add(new RoomPlayer { Id = "2", Name = "Тёма", Status = PlayerStatus.InGame, PingMs = 96 });
            _players.Add(new RoomPlayer { Id = "3", Name = "kostya_2008", Status = PlayerStatus.Joining, PingMs = 180 });
            _players.Add(new RoomPlayer { Id = "4", Name = "Денис", Status = PlayerStatus.Left, LeftAt = DateTime.Now.AddMinutes(-2) });
        }

        public IEnumerable<ChatMessage> MockupChat() => new[]
        {
            new ChatMessage("Марина", "Я на месте, бегу к пляжу", false),
            new ChatMessage("Тёма", "у меня 96 мс, вроде норм", false),
            new ChatMessage("Вы", "Через пять минут рейд, не расходитесь", true),
            new ChatMessage("kostya_2008", "щас, загружаюсь", false),
        };

        public Task SendMessageAsync(string text) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
    }

    internal sealed class PreviewJoinedRoom : IJoinedRoom
    {
        public PreviewJoinedRoom(GameIntegration game, string hostName, string playerName)
        {
            Game = game;
            HostName = hostName;
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, game.Id == "rust" ? 51357 : 58703);
            Players = new[]
            {
                new RoomPlayer { Id = "0", Name = hostName, Status = PlayerStatus.Host },
                new RoomPlayer { Id = "1", Name = "Марина", Status = PlayerStatus.InGame, PingMs = 34 },
                new RoomPlayer { Id = "2", Name = playerName, Status = PlayerStatus.InGame, PingMs = 42, IsYou = true },
            };
        }

        public string Address => "203.0.113.5:47312";
        public GameIntegration Game { get; }
        public string HostName { get; }
        public IPEndPoint? LocalEndPoint { get; }
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
