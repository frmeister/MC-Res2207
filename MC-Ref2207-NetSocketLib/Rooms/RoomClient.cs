// MCTunnel.Core.Network/Rooms/RoomClient.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;
using MCTunnel.Core.Network;

namespace MCTunnel.Core.Rooms
{
    public sealed class RoomClientOptions
    {
        public required string PlayerName { get; init; }

        /// <summary>
        /// Локальный UDP-порт; 0 — любой свободный. С постоянным портом внешний адрес игрока не меняется
        /// между попытками — хосту, который впускает игрока по адресу, не нужно вводить его заново.
        /// Порт занят — берётся любой свободный.
        /// </summary>
        public int LocalPort { get; init; }

        public IPAddress BindAddress { get; init; } = IPAddress.Any;

        /// <summary>Сколько ждать ответа хоста при первом подключении.</summary>
        public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Сколько ждать ответа хоста при переподключении.</summary>
        public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

        public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>Хост не отвечает на пинг дольше — связь считается потерянной.</summary>
        public TimeSpan LostAfter { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Узнавать свой внешний адрес через STUN перед подключением (нужно, чтобы отличить «нет интернета» от «хост не отвечает»).
        /// null — только если хост в интернете, а не в локальной сети.
        /// </summary>
        public bool? DiscoverPublicAddress { get; init; }
    }

    public enum RoomJoinStage
    {
        FindingAddress,
        ContactingHost,
        Done,
    }

    /// <summary>Этап подключения; PublicEndPoint — свой внешний адрес, как только его сообщил STUN.</summary>
    public sealed record RoomJoinProgress(RoomJoinStage Stage, IPEndPoint? PublicEndPoint);

    public enum RoomConnectError
    {
        /// <summary>Хост не ответил: комната закрыта, адрес неверный или сеть хоста не пропускает входящие подключения.</summary>
        HostNotResponding,

        /// <summary>STUN-серверы тоже не ответили — нет интернета или исходящий UDP заблокирован.</summary>
        NoInternet,

        RoomFull,
        IncompatibleVersion,
    }

    public sealed class RoomConnectException : Exception
    {
        public RoomConnectException(RoomConnectError error, IReadOnlyList<string> details, IPEndPoint? publicEndPoint = null)
            : base(error.ToString())
        {
            Error = error;
            Details = details;
            PublicEndPoint = publicEndPoint;
        }

        public RoomConnectError Error { get; }

        /// <summary>Свой внешний адрес — его хост вводит в «Впустить по адресу», если его сеть не пропускает входящие.</summary>
        public IPEndPoint? PublicEndPoint { get; }

        /// <summary>Технические подробности для пользователя и поддержки.</summary>
        public IReadOnlyList<string> Details { get; }
    }

    /// <summary>
    /// Подключение к комнате по адресу хоста IP:порт.
    /// Игра подключается к портам из OpenedPorts (127.0.0.1:порт) — трафик идёт через туннель к серверу хоста.
    /// Пинг до хоста меряется датаграммами раз в PingInterval. Если хост долго не отвечает, поднимается ConnectionLost,
    /// и ReconnectAsync подключается заново на тот же адрес, стараясь сохранить прежние локальные порты.
    /// События вызываются в потоках пула.
    /// </summary>
    public sealed class RoomClient : IDisposable
    {
        private const double PingSmoothing = 0.3;
        private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PortsTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StunTimeout = TimeSpan.FromSeconds(5);

        private readonly RoomClientOptions _options;
        private readonly UdpPeer _peer;
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<ForwardRule, OpenedPort> _openedPorts = new(); // под _sync
        private List<RoomMember> _members = new();
        private ReliableChannel? _channel;
        private TunnelSession? _tunnel;
        private TaskCompletionSource<byte[]>? _welcomeTcs;
        private TaskCompletionSource<bool>? _portsTcs;
        private double? _pingMs;
        private long _lastPongTicks;
        private uint _pingNumber;
        private bool _lost;
        private int _disposed;

        public event EventHandler? MembersChanged;
        public event EventHandler<RoomChatMessage>? ChatReceived;
        public event EventHandler? PingChanged;
        public event EventHandler? PortsChanged;

        /// <summary>Хост перестал отвечать. Дальше — ReconnectAsync или Leave.</summary>
        public event EventHandler? ConnectionLost;

        /// <summary>Хост закрыл комнату.</summary>
        public event EventHandler? RoomClosed;

        private RoomClient(IPEndPoint hostEndPoint, RoomClientOptions options, UdpPeer peer)
        {
            HostEndPoint = hostEndPoint;
            _options = options;
            _peer = peer;
            PlayerName = RoomProtocol.CleanName(options.PlayerName);
        }

        public IPEndPoint HostEndPoint { get; }
        public string PlayerName { get; }
        public int MemberId { get; private set; }
        public string HostName { get; private set; } = "";
        public string GameId { get; private set; } = "";
        public int MaxPlayers { get; private set; }

        /// <summary>Свой внешний адрес по STUN (null — не узнавали или не ответил).</summary>
        public StunResult? Stun { get; private set; }

        /// <summary>Сглаженный пинг до хоста в миллисекундах; null — ещё не измерен.</summary>
        public int? PingMs
        {
            get { lock (_sync) { return _pingMs is double p ? (int)Math.Round(p) : null; } }
        }

        public IReadOnlyList<RoomMember> Members
        {
            get { lock (_sync) { return _members; } }
        }

        /// <summary>Порты серверов хоста, открытые на этом компьютере.</summary>
        public IReadOnlyList<OpenedPort> OpenedPorts
        {
            get { lock (_sync) { return _openedPorts.Values.ToList(); } }
        }

        public bool IsConnected
        {
            get { lock (_sync) { return !_lost && _channel?.State == ConnectionState.Established; } }
        }

        public static async Task<RoomClient> JoinAsync(IPEndPoint hostEndPoint, RoomClientOptions options,
            IProgress<RoomJoinProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (hostEndPoint == null) throw new ArgumentNullException(nameof(hostEndPoint));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var peer = OpenPeer(options);
            var client = new RoomClient(hostEndPoint, options, peer);

            try
            {
                progress?.Report(new RoomJoinProgress(RoomJoinStage.FindingAddress, null));
                bool useStun = options.DiscoverPublicAddress ?? !IsLocalNetwork(hostEndPoint.Address);
                if (useStun)
                {
                    using var stunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stunCts.CancelAfter(StunTimeout);
                    try
                    {
                        client.Stun = await StunClient.DiscoverAsync(peer, cancellationToken: stunCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Не успели — считаем, что STUN не ответил
                    }
                }

                progress?.Report(new RoomJoinProgress(RoomJoinStage.ContactingHost, client.Stun?.PublicEndPoint));
                await client.ConnectAsync(options.HandshakeTimeout, useStun, cancellationToken);

                progress?.Report(new RoomJoinProgress(RoomJoinStage.Done, client.Stun?.PublicEndPoint));
                Trace.WriteLine($"[Core.Rooms.Client] Joined room of '{client.HostName}' at {hostEndPoint}, game '{client.GameId}', member {client.MemberId}");
                _ = client.PingLoopAsync();
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendChatAsync(string text)
        {
            text = RoomProtocol.CleanChat(text);
            if (text.Length == 0) return;
            await SendAsync(RoomProtocol.BuildChatSend(text));
        }

        /// <summary>Подключается к хосту заново после ConnectionLost. false — хост так и не ответил.</summary>
        public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected) return true;
            try
            {
                await ConnectAsync(_options.ReconnectTimeout, checkedInternet: false, cancellationToken);
                Trace.WriteLine($"[Core.Rooms.Client] Reconnected to {HostEndPoint}");
                return true;
            }
            catch (RoomConnectException ex)
            {
                Trace.WriteLine($"[Core.Rooms.Client] Reconnect to {HostEndPoint} failed: {ex.Error}");
                return false;
            }
        }

        /// <summary>Сообщает хосту о выходе и закрывает соединение.</summary>
        public async Task LeaveAsync()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            await SendAsync(RoomProtocol.BuildLeave());
            await Task.Delay(300); // Даём кадру уйти
            Dispose();
        }

        // -- Подключение: рукопожатие, Join, ответ хоста --
        private async Task ConnectAsync(TimeSpan timeout, bool checkedInternet, CancellationToken cancellationToken)
        {
            Dictionary<ForwardRule, int> preferredPorts;
            lock (_sync)
            {
                DetachChannel();
                preferredPorts = _openedPorts.ToDictionary(p => p.Key, p => p.Value.LocalEndPoint.Port);
            }

            var channel = new ReliableChannel(_peer);
            var tunnel = new TunnelSession(channel, preferredPorts);
            var welcomeTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var portsTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _channel = channel;
                _tunnel = tunnel;
                _welcomeTcs = welcomeTcs;
                _portsTcs = portsTcs;
            }
            channel.DataReceived += OnFrame;
            channel.DatagramReceived += OnDatagram;
            channel.Closed += OnChannelClosed;
            tunnel.RemotePortOpened += OnPortOpened;

            bool connected = await channel.ConnectAsync(HostEndPoint, (int)timeout.TotalMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!connected)
            {
                DetachAndDispose(channel, tunnel);
                bool noInternet = checkedInternet && Stun == null;
                throw new RoomConnectException(noInternet ? RoomConnectError.NoInternet : RoomConnectError.HostNotResponding,
                    Diagnostics($"Хост {HostEndPoint}: нет ответа за {timeout.TotalSeconds:0} с"), Stun?.PublicEndPoint);
            }

            await channel.SendDataAsync(RoomProtocol.BuildJoin(PlayerName), cancellationToken);

            byte[] answer;
            try
            {
                answer = await welcomeTcs.Task.WaitAsync(WelcomeTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                DetachAndDispose(channel, tunnel);
                throw new RoomConnectException(RoomConnectError.HostNotResponding,
                    Diagnostics($"Хост {HostEndPoint}: соединение есть, но комната не ответила"), Stun?.PublicEndPoint);
            }

            if (answer[0] == RoomProtocol.Reject)
            {
                DetachAndDispose(channel, tunnel);
                RoomProtocol.TryParseReject(answer, out var reason);
                throw new RoomConnectException(reason == RoomRejectReason.RoomFull ? RoomConnectError.RoomFull : RoomConnectError.IncompatibleVersion,
                    Diagnostics($"Хост отказал: {reason}"));
            }

            if (!RoomProtocol.TryParseWelcome(answer, out var version, out var welcome) || welcome == null || version != RoomProtocol.Version)
            {
                DetachAndDispose(channel, tunnel);
                throw new RoomConnectException(RoomConnectError.IncompatibleVersion, Diagnostics($"Версия протокола хоста: {version}"));
            }

            MemberId = welcome.MemberId;
            HostName = welcome.HostName;
            GameId = welcome.GameId;
            MaxPlayers = welcome.MaxPlayers;

            // Хост открывает порты сразу после Welcome — ждём их, чтобы игроку сразу показать адрес для игры
            if (welcome.ShareCount > 0)
            {
                try
                {
                    await portsTcs.Task.WaitAsync(PortsTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    Trace.WriteLine("[Core.Rooms.Client] Host announced ports, but none opened yet");
                }
            }

            // Отсчёт тишины для обнаружения потери связи — с момента, когда подключение готово
            lock (_sync)
            {
                _lost = false;
                _lastPongTicks = Environment.TickCount64;
            }
        }

        private List<string> Diagnostics(string first)
        {
            var details = new List<string> { first };
            if (Stun != null)
            {
                string nat = Stun.IsSymmetricNat switch { true => "симметричный NAT", false => "обычный NAT", _ => "тип NAT не определён" };
                details.Add($"Ваш внешний адрес: {Stun.PublicEndPoint} ({nat})");
            }
            else
            {
                details.Add(IsLocalNetwork(HostEndPoint.Address) ? "Хост в локальной сети, STUN не нужен" : "STUN-серверы: нет ответа");
            }
            return details;
        }

        // -- Кадры от хоста --
        private void OnFrame(object? sender, byte[] frame)
        {
            if (!RoomProtocol.IsRoomFrame(frame) || !ReferenceEquals(sender, _channel)) return;

            switch (frame[0])
            {
                case RoomProtocol.Welcome:
                case RoomProtocol.Reject:
                    _welcomeTcs?.TrySetResult(frame);
                    break;
                case RoomProtocol.Roster:
                    if (RoomProtocol.TryParseRoster(frame, out var members))
                    {
                        lock (_sync) { _members = members; }
                        Raise(MembersChanged);
                    }
                    break;
                case RoomProtocol.Chat:
                    // Свои сообщения приложение показывает сразу при отправке — эхо от хоста пропускаем
                    if (RoomProtocol.TryParseChat(frame, out var message) && message!.AuthorId != MemberId)
                        Raise(ChatReceived, message);
                    break;
                case RoomProtocol.Closed:
                    Trace.WriteLine("[Core.Rooms.Client] Host closed the room");
                    lock (_sync) { DetachChannel(); }
                    Raise(RoomClosed);
                    break;
            }
        }

        private void OnDatagram(object? sender, byte[] frame)
        {
            if (frame.Length == 0) return;
            if (frame[0] == RoomProtocol.Ping)
            {
                TrySendDatagram(RoomProtocol.BuildPong(frame));
            }
            else if (RoomProtocol.TryParsePong(frame, out _, out var sentTicks))
            {
                long now = Environment.TickCount64;
                int rtt = (int)Math.Max(0, now - sentTicks);
                lock (_sync)
                {
                    _lastPongTicks = now;
                    _pingMs = _pingMs is double old ? old + PingSmoothing * (rtt - old) : rtt;
                }
                Raise(PingChanged);
            }
        }

        private void OnPortOpened(object? sender, OpenedPort port)
        {
            lock (_sync) { _openedPorts[port.RemoteRule] = port; }
            _portsTcs?.TrySetResult(true);
            Raise(PortsChanged);
        }

        private void OnChannelClosed(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, _channel)) return;
            MarkLost("channel closed");
        }

        private async Task PingLoopAsync()
        {
            using var timer = new PeriodicTimer(_options.PingInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    bool lost;
                    long silentMs;
                    lock (_sync)
                    {
                        lost = _lost || _channel == null;
                        silentMs = Environment.TickCount64 - _lastPongTicks;
                    }
                    if (lost) continue;

                    if (silentMs > _options.LostAfter.TotalMilliseconds)
                    {
                        MarkLost($"no pong for {silentMs} ms");
                        continue;
                    }
                    TrySendDatagram(RoomProtocol.BuildPing(++_pingNumber, Environment.TickCount64));
                }
            }
            catch (OperationCanceledException)
            {
                // Вышли из комнаты
            }
        }

        private void MarkLost(string reason)
        {
            lock (_sync)
            {
                if (_lost || Volatile.Read(ref _disposed) != 0) return;
                _lost = true;
                _pingMs = null;
                DetachChannel(); // Локальные порты освобождаются — переподключение займёт их снова
            }
            Trace.WriteLine($"[Core.Rooms.Client] Connection to {HostEndPoint} lost: {reason}");
            Raise(ConnectionLost);
        }

        // -- Отправка --
        private async Task SendAsync(byte[] frame)
        {
            var channel = _channel;
            if (channel == null) return;
            try
            {
                await channel.SendDataAsync(frame);
            }
            catch (InvalidOperationException)
            {
                // Соединение закрыто
            }
        }

        private void TrySendDatagram(byte[] frame)
        {
            try
            {
                _ = _channel?.SendDatagramAsync(frame);
            }
            catch (InvalidOperationException)
            {
            }
        }

        // Вызывается под _sync
        private void DetachChannel()
        {
            var channel = _channel;
            var tunnel = _tunnel;
            _channel = null;
            _tunnel = null;
            if (channel != null && tunnel != null) DetachAndDispose(channel, tunnel);
        }

        private void DetachAndDispose(ReliableChannel channel, TunnelSession tunnel)
        {
            channel.DataReceived -= OnFrame;
            channel.DatagramReceived -= OnDatagram;
            channel.Closed -= OnChannelClosed;
            tunnel.RemotePortOpened -= OnPortOpened;
            tunnel.Dispose();
            channel.Dispose();
        }

        private void Raise(EventHandler? handler)
        {
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Trace.WriteLine($"[Core.Rooms.Client] Event handler threw exception: {ex}"); }
        }

        private void Raise<T>(EventHandler<T>? handler, T args)
        {
            if (handler == null) return;
            try { handler(this, args); }
            catch (Exception ex) { Trace.WriteLine($"[Core.Rooms.Client] Event handler threw exception: {ex}"); }
        }

        private static UdpPeer OpenPeer(RoomClientOptions options)
        {
            UdpPeer? peer = null;
            if (options.LocalPort != 0)
            {
                try
                {
                    peer = new UdpPeer(new IPEndPoint(options.BindAddress, options.LocalPort));
                }
                catch (SocketException)
                {
                    // Порт занят (например, на этом же компьютере открыта комната) — берём любой
                    Trace.WriteLine($"[Core.Rooms.Client] UDP {options.LocalPort} is busy, using a random port");
                }
            }

            peer ??= new UdpPeer(new IPEndPoint(options.BindAddress, 0));
            _ = Task.Run(() => peer.StartReceivingAsync());
            return peer;
        }

        /// <summary>Адрес из локальной сети, VPN или этого компьютера — внешний адрес для подключения не нужен.</summary>
        public static bool IsLocalNetwork(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // Общие адреса провайдеров и игровых VPN
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            lock (_sync) { DetachChannel(); }
            _peer.Dispose();
        }
    }
}
