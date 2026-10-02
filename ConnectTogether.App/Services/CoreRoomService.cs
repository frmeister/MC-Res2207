// ConnectTogether.App/Services/CoreRoomService.cs

using System.Diagnostics;
using System.Net;
using ConnectTogether.App.Integrations;
using ConnectTogether.App.Models;
using MCTunnel.Core.Network;
using MCTunnel.Core.Rooms;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Комнаты через Core: хост — RoomHost на порту из настроек, игрок — RoomClient по введённому адресу.
    /// Core вызывает события в потоках пула; здесь они переносятся в поток интерфейса.
    /// </summary>
    public sealed class CoreRoomService : IRoomService
    {
        /// <summary>Сколько игрок ждёт хоста: хватает, чтобы отправить ему свой адрес и дождаться «Впустить по адресу».</summary>
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMinutes(2);

        private readonly SettingsStore _settings;
        private readonly IntegrationCatalog _catalog;
        private readonly IPAddress _bindAddress;
        private readonly bool _discoverPublicAddress;

        /// <param name="bindAddress">Адрес сокетов комнаты; по умолчанию все интерфейсы. Тесты берут 127.0.0.1.</param>
        /// <param name="discoverPublicAddress">false — не узнавать внешний адрес через STUN (тесты без интернета).</param>
        public CoreRoomService(SettingsStore settings, IntegrationCatalog catalog, IPAddress? bindAddress = null, bool discoverPublicAddress = true)
        {
            _settings = settings;
            _catalog = catalog;
            _bindAddress = bindAddress ?? IPAddress.Any;
            _discoverPublicAddress = discoverPublicAddress;
        }

        public async Task<IHostedRoom> CreateRoomAsync(GameIntegration game, ServerEndpoint server, string hostName, CancellationToken ct = default)
        {
            var host = await RoomHost.OpenAsync(new RoomHostOptions
            {
                HostName = hostName,
                GameId = game.Id,
                Port = _settings.Current.AppPort,
                Shares = new[] { ToRule(server) },
                BindAddress = _bindAddress,
                DiscoverPublicAddress = _discoverPublicAddress,
            }, ct);
            return new CoreHostedRoom(host, game, server, SynchronizationContext.Current);
        }

        public async Task<IJoinedRoom> JoinRoomAsync(string address, string playerName, IProgress<JoinProgress>? progress, CancellationToken ct = default)
        {
            if (!RoomAddress.TryParse(address, out var hostName, out var port, out var error))
                throw new RoomJoinException(JoinFailure.HostNotResponding, new[] { error! });
            string display = RoomAddress.Format(hostName, port);

            var endPoint = await RoomAddress.ResolveAsync(hostName, port, ct);
            if (endPoint == null)
                throw new RoomJoinException(JoinFailure.HostNotResponding, new[] { $"Имя {hostName} не найдено в DNS" });

            // Progress создан в потоке интерфейса — этапы приходят туда же
            var coreProgress = new Progress<RoomJoinProgress>(p => progress?.Report(new JoinProgress(p.Stage switch
            {
                RoomJoinStage.FindingAddress => JoinStage.FindingAddress,
                RoomJoinStage.ContactingHost => JoinStage.ContactingHost,
                _ => JoinStage.Done,
            }, null, p.PublicEndPoint?.ToString())));

            RoomClient client;
            try
            {
                var options = new RoomClientOptions
                {
                    PlayerName = playerName,
                    BindAddress = _bindAddress,
                    // Постоянный порт — постоянный внешний адрес: хосту не придётся вводить его заново после «Повторить»
                    LocalPort = _settings.Current.AppPort,
                    HandshakeTimeout = HandshakeTimeout,
                    DiscoverPublicAddress = _discoverPublicAddress ? null : false,
                };
                client = await RoomClient.JoinAsync(endPoint, options, coreProgress, ct);
            }
            catch (RoomConnectException ex)
            {
                var failure = ex.Error switch
                {
                    RoomConnectError.NoInternet => JoinFailure.NoInternet,
                    RoomConnectError.RoomFull => JoinFailure.RoomFull,
                    RoomConnectError.IncompatibleVersion => JoinFailure.IncompatibleVersion,
                    _ => JoinFailure.HostNotResponding,
                };
                throw new RoomJoinException(failure, ex.Details, ex.PublicEndPoint?.ToString());
            }

            var game = _catalog.Find(client.GameId) ?? IntegrationCatalog.Unknown(client.GameId);
            progress?.Report(new JoinProgress(JoinStage.Done, new RoomInfo(display, game, client.HostName)));
            return new CoreJoinedRoom(client, display, game, SynchronizationContext.Current);
        }

        internal static ForwardRule ToRule(ServerEndpoint server) =>
            new(server.Protocol == EndpointProtocol.Udp ? ForwardProtocol.Udp : ForwardProtocol.Tcp, server.Port);

        internal static RoomPlayer ToPlayer(RoomMember member, bool isYou) => new()
        {
            Id = member.Id.ToString(),
            Name = member.Name,
            IsYou = isYou,
            PingMs = member.PingMs,
            LeftAt = member.LeftAt,
            Status = member.Status switch
            {
                RoomMemberStatus.Host => PlayerStatus.Host,
                RoomMemberStatus.InGame => PlayerStatus.InGame,
                RoomMemberStatus.InRoom => PlayerStatus.InRoom,
                RoomMemberStatus.Left => PlayerStatus.Left,
                _ => PlayerStatus.Joining,
            },
        };
    }

    /// <summary>Переносит вызовы событий Core в поток интерфейса.</summary>
    internal sealed class UiDispatch
    {
        private readonly SynchronizationContext? _context;

        public UiDispatch(SynchronizationContext? context) => _context = context;

        public void Post(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }
    }

    internal sealed class CoreHostedRoom : IHostedRoom
    {
        private readonly RoomHost _host;
        private readonly UiDispatch _ui;

        public CoreHostedRoom(RoomHost host, GameIntegration game, ServerEndpoint server, SynchronizationContext? context)
        {
            _host = host;
            _ui = new UiDispatch(context);
            Game = game;
            Server = server;
            _host.MembersChanged += (_, _) => _ui.Post(() => PlayersChanged?.Invoke(this, EventArgs.Empty));
            _host.ChatReceived += (_, m) => _ui.Post(() => MessageReceived?.Invoke(this, new ChatMessage(m.AuthorName, m.Text, false)));
            _host.PublicEndPointChanged += (_, _) => _ui.Post(() => AddressChanged?.Invoke(this, EventArgs.Empty));
        }

        public string Address => _host.PublicEndPoint?.ToString() ?? LanAddress ?? $"127.0.0.1:{LocalPort}";
        public GameIntegration Game { get; }
        public ServerEndpoint Server { get; }
        public DateTime OpenedAt { get; } = DateTime.Now;
        public int MaxPlayers => _host.MaxPlayers;
        public int LocalPort => _host.Port;
        public IPAddress? PublicAddress => _host.PublicEndPoint?.Address;
        public string? LanAddress => _host.LanEndPoints.FirstOrDefault()?.ToString();
        public bool? IsSymmetricNat => _host.IsSymmetricNat;

        public void LetIn(IPEndPoint player) => _host.LetIn(player);

        public IReadOnlyList<RoomPlayer> Players => _host.Members.Select(m => CoreRoomService.ToPlayer(m, m.Id == 0)).ToList();

        public event EventHandler? PlayersChanged;
        public event EventHandler<ChatMessage>? MessageReceived;
        public event EventHandler? AddressChanged;

        public Task SendMessageAsync(string text) => _host.SendChatAsync(text);

        public async Task CloseAsync()
        {
            await _host.CloseAsync();
            Trace.WriteLine($"[CoreRoomService] Hosted room on port {LocalPort} closed");
        }
    }

    internal sealed class CoreJoinedRoom : IJoinedRoom
    {
        private readonly RoomClient _client;
        private readonly UiDispatch _ui;

        public CoreJoinedRoom(RoomClient client, string address, GameIntegration game, SynchronizationContext? context)
        {
            _client = client;
            _ui = new UiDispatch(context);
            Address = address;
            Game = game;
            _client.MembersChanged += (_, _) => _ui.Post(() => PlayersChanged?.Invoke(this, EventArgs.Empty));
            _client.ChatReceived += (_, m) => _ui.Post(() => MessageReceived?.Invoke(this, new ChatMessage(m.AuthorName, m.Text, false)));
            _client.PingChanged += (_, _) => _ui.Post(() => PingChanged?.Invoke(this, EventArgs.Empty));
            _client.PortsChanged += (_, _) => _ui.Post(() => LocalEndPointChanged?.Invoke(this, EventArgs.Empty));
            _client.ConnectionLost += (_, _) => _ui.Post(() => ConnectionLost?.Invoke(this, EventArgs.Empty));
            _client.RoomClosed += (_, _) => _ui.Post(() => Closed?.Invoke(this, EventArgs.Empty));
        }

        public string Address { get; }
        public GameIntegration Game { get; }
        public string HostName => _client.HostName;
        public int? PingMs => _client.PingMs;

        // Хост открывает порт сервера игры; если их несколько, игре нужен первый — порт из интеграции
        public IPEndPoint? LocalEndPoint => _client.OpenedPorts.FirstOrDefault()?.LocalEndPoint;

        public IReadOnlyList<RoomPlayer> Players => _client.Members
            .Where(m => m.Status != RoomMemberStatus.Left)
            .Select(m => CoreRoomService.ToPlayer(m, m.Id == _client.MemberId))
            .ToList();

        public event EventHandler? PlayersChanged;
        public event EventHandler<ChatMessage>? MessageReceived;
        public event EventHandler? PingChanged;
        public event EventHandler? LocalEndPointChanged;
        public event EventHandler? ConnectionLost;
        public event EventHandler? Closed;

        public Task SendMessageAsync(string text) => _client.SendChatAsync(text);

        public Task<bool> ReconnectAsync(CancellationToken ct = default) => _client.ReconnectAsync(ct);

        public Task LeaveAsync() => _client.LeaveAsync();
    }
}
