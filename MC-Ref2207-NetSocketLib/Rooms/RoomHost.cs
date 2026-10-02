// MCTunnel.Core.Network/Rooms/RoomHost.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;
using MCTunnel.Core.Network;

namespace MCTunnel.Core.Rooms
{
    public sealed class RoomHostOptions
    {
        /// <summary>Имя хоста в комнате.</summary>
        public required string HostName { get; init; }

        /// <summary>Идентификатор интеграции игры — Core его не разбирает, только передаёт игрокам.</summary>
        public required string GameId { get; init; }

        /// <summary>Порты серверов этого компьютера, которые открываются каждому игроку.</summary>
        public IReadOnlyList<ForwardRule> Shares { get; init; } = Array.Empty<ForwardRule>();

        /// <summary>UDP-порт комнаты; его же пробрасывают на роутере. 0 — любой свободный.</summary>
        public int Port { get; init; }

        public IPAddress BindAddress { get; init; } = IPAddress.Any;

        /// <summary>Сколько человек в комнате вместе с хостом.</summary>
        public int MaxPlayers { get; init; } = 10;

        /// <summary>Узнавать внешний адрес через STUN (в тестах на 127.0.0.1 не нужно).</summary>
        public bool DiscoverPublicAddress { get; init; } = true;

        public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>Сколько показывать вышедших участников в списке.</summary>
        public TimeSpan KeepLeftMembers { get; init; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Комната на этом компьютере: один UDP-порт, к которому подключаются игроки по адресу IP:порт.
    /// С каждым игроком — свой ReliableChannel (все на одном UdpPeer: канал отбирает пакеты по адресу партнёра)
    /// и свой TunnelSession, через который его игра ходит к серверам из Shares.
    /// Хост ведёт список участников, пересылает чат всем и раз в PingInterval меряет пинг до каждого игрока.
    /// События вызываются в потоках пула.
    /// </summary>
    public sealed class RoomHost : IDisposable
    {
        private const int HostMemberId = 0;
        private const int MaxPendingConnections = 8;
        private const int HandshakeTimeoutMs = 10000;
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StunTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MappingKeepAliveInterval = TimeSpan.FromSeconds(20);

        private readonly RoomHostOptions _options;
        private readonly UdpPeer _peer;
        private readonly ConcurrentDictionary<IPEndPoint, Connection> _connections = new();
        private readonly object _sync = new();
        private readonly Dictionary<int, MemberState> _members = new(); // под _sync, без хоста
        private readonly CancellationTokenSource _cts = new();
        private byte[]? _lastRoster;
        private int _nextMemberId = 1;
        private uint _pingNumber;
        private int _closed;

        public event EventHandler? MembersChanged;
        public event EventHandler<RoomChatMessage>? ChatReceived;

        /// <summary>Внешний адрес сменился (NAT выдал другой порт) — его надо заново отправить друзьям.</summary>
        public event EventHandler? PublicEndPointChanged;

        private RoomHost(RoomHostOptions options, UdpPeer peer)
        {
            _options = options;
            _peer = peer;
            HostName = RoomProtocol.CleanName(options.HostName);
            _peer.DataReceived += OnRawDataReceived;
        }

        public string HostName { get; }
        public string GameId => _options.GameId;
        public int MaxPlayers => _options.MaxPlayers;

        /// <summary>Порт комнаты на этом компьютере.</summary>
        public int Port => _peer.LocalPort;

        /// <summary>Внешний адрес комнаты (STUN) — его вводят друзья из интернета. null, если STUN не ответил.</summary>
        public IPEndPoint? PublicEndPoint { get; private set; }

        /// <summary>STUN-сервер, через который узнали внешний адрес; им же держим маппинг NAT.</summary>
        private IPEndPoint? _stunServer;

        /// <summary>
        /// true — NAT меняет внешний порт для каждого адресата (симметричный): друзья из интернета
        /// не подключатся без проброса порта. null — проверить не удалось.
        /// </summary>
        public bool? IsSymmetricNat { get; private set; }

        /// <summary>Адреса комнаты в локальных сетях этого компьютера — для друзей в той же сети.</summary>
        public IReadOnlyList<IPEndPoint> LanEndPoints => LocalIPv4Addresses().Select(a => new IPEndPoint(a, Port)).ToList();

        /// <summary>Участники: хост первым, затем игроки по порядку входа.</summary>
        public IReadOnlyList<RoomMember> Members
        {
            get
            {
                lock (_sync) { return SnapshotMembers(); }
            }
        }

        /// <summary>
        /// Открывает комнату: занимает UDP-порт и узнаёт внешний адрес.
        /// SocketException — порт занят другой программой.
        /// </summary>
        public static async Task<RoomHost> OpenAsync(RoomHostOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var peer = new UdpPeer(new IPEndPoint(options.BindAddress, options.Port));
            _ = Task.Run(() => peer.StartReceivingAsync());
            var host = new RoomHost(options, peer);

            try
            {
                if (options.DiscoverPublicAddress)
                {
                    using var stunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stunCts.CancelAfter(StunTimeout);
                    try
                    {
                        var stun = await StunClient.DiscoverAsync(peer, cancellationToken: stunCts.Token);
                        if (stun != null)
                        {
                            host.PublicEndPoint = stun.PublicEndPoint;
                            host.IsSymmetricNat = stun.IsSymmetricNat;
                            host._stunServer = stun.Server;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // STUN не успел — комната работает в локальной сети, адрес из интернета неизвестен
                    }
                }
            }
            catch
            {
                host.Dispose();
                throw;
            }

            Trace.WriteLine($"[Core.Rooms.Host] Room '{options.GameId}' opened on port {host.Port}, public {host.PublicEndPoint?.ToString() ?? "unknown"}, symmetric NAT={host.IsSymmetricNat}");
            _ = host.PingLoopAsync();
            _ = host.KeepMappingAliveAsync();
            return host;
        }

        public async Task SendChatAsync(string text)
        {
            text = RoomProtocol.CleanChat(text);
            if (text.Length == 0) return;
            await BroadcastAsync(RoomProtocol.BuildChat(new RoomChatMessage(HostMemberId, HostName, text)));
        }

        /// <summary>Закрывает комнату: игроки получают «комната закрыта», соединения закрываются.</summary>
        public async Task CloseAsync()
        {
            if (Volatile.Read(ref _closed) != 0) return;
            await BroadcastAsync(RoomProtocol.BuildClosed());
            await Task.Delay(300); // Даём кадру уйти, прежде чем закрыть каналы
            Dispose();
        }

        // -- Приём новых игроков --
        // Игрок шлёт Hello своего ReliableChannel на адрес комнаты. Для незнакомого адреса создаём канал, и он завершает
        // рукопожатие на следующем Hello игрока (тот повторяет их раз в секунду). Пакеты знакомых адресов разбирают их каналы.
        private void OnRawDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            if (e.Data.Length < Packet.PacketHeaderSizes.Total || e.Data[0] != (byte)PacketType.Hello) return;
            if (_connections.ContainsKey(e.RemoteEndPoint)) return;
            if (_connections.Values.Count(c => c.MemberId == null) >= MaxPendingConnections) return;

            var connection = new Connection(this, e.RemoteEndPoint);
            if (!_connections.TryAdd(e.RemoteEndPoint, connection))
            {
                connection.Dispose();
                return;
            }

            Trace.WriteLine($"[Core.Rooms.Host] Incoming connection from {e.RemoteEndPoint}");
            _ = AcceptAsync(connection);
        }

        private async Task AcceptAsync(Connection connection)
        {
            bool connected;
            try
            {
                connected = await connection.Channel.ConnectAsync(connection.EndPoint, HandshakeTimeoutMs, _cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                connected = false;
            }

            if (!connected)
            {
                Trace.WriteLine($"[Core.Rooms.Host] Handshake with {connection.EndPoint} failed");
                RemoveConnection(connection);
                return;
            }

            // Игрок должен представиться (Join), иначе освобождаем место для других
            try
            {
                await Task.Delay(JoinTimeout, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (connection.MemberId == null)
            {
                Trace.WriteLine($"[Core.Rooms.Host] {connection.EndPoint} did not join in time");
                RemoveConnection(connection);
            }
        }

        // -- Кадры от игрока --
        private void OnFrame(Connection connection, byte[] frame)
        {
            if (!RoomProtocol.IsRoomFrame(frame)) return; // Кадры пересылки портов разбирает TunnelSession

            switch (frame[0])
            {
                case RoomProtocol.Join:
                    HandleJoin(connection, frame);
                    break;
                case RoomProtocol.ChatSend when connection.MemberId is int authorId:
                    if (RoomProtocol.TryParseChatSend(frame, out var text))
                    {
                        string author;
                        lock (_sync) { author = _members.TryGetValue(authorId, out var m) ? m.Name : "Игрок"; }
                        var message = new RoomChatMessage(authorId, author, text);
                        Raise(ChatReceived, message);
                        _ = BroadcastAsync(RoomProtocol.BuildChat(message));
                    }
                    break;
                case RoomProtocol.Leave:
                    Trace.WriteLine($"[Core.Rooms.Host] {connection.EndPoint} left the room");
                    RemoveConnection(connection);
                    break;
            }
        }

        private void HandleJoin(Connection connection, byte[] frame)
        {
            if (!RoomProtocol.TryParseJoin(frame, out var version, out var name)) return;

            if (version != RoomProtocol.Version)
            {
                Trace.WriteLine($"[Core.Rooms.Host] {connection.EndPoint} has protocol version {version}, rejecting");
                _ = RejectAsync(connection, RoomRejectReason.IncompatibleVersion);
                return;
            }

            int memberId;
            Connection? replaced = null;
            lock (_sync)
            {
                if (connection.MemberId is int existing)
                {
                    // Повторный Join по тому же каналу: игрок переподключился, пока наш канал был жив
                    memberId = existing;
                    connection.ResetTunnel();
                }
                else
                {
                    // Тот же человек с того же IP (например, перезапустил приложение) — занимает своё прежнее место
                    var previous = _members.Values.FirstOrDefault(m => m.Name == name &&
                        (m.Connection == null || m.Connection.EndPoint.Address.Equals(connection.EndPoint.Address)));
                    int active = _members.Values.Count(m => m.Status != RoomMemberStatus.Left);
                    if (previous == null && active + 1 >= _options.MaxPlayers)
                    {
                        memberId = -1;
                    }
                    else
                    {
                        if (previous != null)
                        {
                            replaced = previous.Connection;
                            memberId = previous.Id;
                        }
                        else
                        {
                            memberId = _nextMemberId++;
                        }
                        _members[memberId] = new MemberState(memberId, name, connection);
                        connection.MemberId = memberId;
                    }
                }
            }

            if (memberId < 0)
            {
                Trace.WriteLine($"[Core.Rooms.Host] Room is full, rejecting {connection.EndPoint}");
                _ = RejectAsync(connection, RoomRejectReason.RoomFull);
                return;
            }

            if (replaced != null && replaced != connection)
            {
                replaced.MemberId = null; // Место уже занято новым подключением — закрытие старого его не трогает
                RemoveConnection(replaced);
            }

            Trace.WriteLine($"[Core.Rooms.Host] '{name}' joined as member {memberId} from {connection.EndPoint}");
            _ = WelcomeAsync(connection, memberId);
            PublishRoster();
        }

        private async Task WelcomeAsync(Connection connection, int memberId)
        {
            try
            {
                await connection.Channel.SendDataAsync(RoomProtocol.BuildWelcome(new RoomWelcome(memberId, _options.MaxPlayers, _options.GameId, HostName, _options.Shares.Count)));
                if (_options.Shares.Count > 0) await connection.Tunnel.ShareAsync(_options.Shares);
                await connection.Channel.SendDataAsync(_lastRoster ?? RoomProtocol.BuildRoster(Members));
            }
            catch (InvalidOperationException)
            {
                // Канал успел закрыться
            }
        }

        private async Task RejectAsync(Connection connection, RoomRejectReason reason)
        {
            try
            {
                await connection.Channel.SendDataAsync(RoomProtocol.BuildReject(reason));
                await Task.Delay(500);
            }
            catch (InvalidOperationException)
            {
            }
            RemoveConnection(connection);
        }

        private void OnDatagram(Connection connection, byte[] frame)
        {
            if (frame.Length == 0) return;
            if (frame[0] == RoomProtocol.Ping)
            {
                TrySendDatagram(connection, RoomProtocol.BuildPong(frame));
            }
            else if (RoomProtocol.TryParsePong(frame, out _, out var sentTicks))
            {
                int rtt = (int)Math.Max(0, Environment.TickCount64 - sentTicks);
                bool firstSample = false;
                lock (_sync)
                {
                    if (connection.MemberId is int id && _members.TryGetValue(id, out var member))
                    {
                        firstSample = member.UpdatePing(rtt);
                    }
                }
                // Новые значения пинга уходят со следующей рассылкой из PingLoop; сразу — только первый замер,
                // чтобы «подключается» сменилось на «в комнате» без задержки
                if (firstSample) PublishRoster();
            }
        }

        private void OnChannelClosed(Connection connection)
        {
            Trace.WriteLine($"[Core.Rooms.Host] Connection with {connection.EndPoint} closed");
            RemoveConnection(connection);
        }

        private void RemoveConnection(Connection connection)
        {
            if (!_connections.TryRemove(new KeyValuePair<IPEndPoint, Connection>(connection.EndPoint, connection))) return;
            connection.Dispose();

            bool changed = false;
            lock (_sync)
            {
                if (connection.MemberId is int id && _members.TryGetValue(id, out var member) && member.Connection == connection)
                {
                    member.MarkLeft();
                    changed = true;
                }
            }
            if (changed) PublishRoster();
        }

        // -- Пинг, статусы и список участников --
        private async Task PingLoopAsync()
        {
            using var timer = new PeriodicTimer(_options.PingInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    var ping = RoomProtocol.BuildPing(++_pingNumber, Environment.TickCount64);
                    foreach (var connection in _connections.Values)
                    {
                        if (connection.MemberId != null) TrySendDatagram(connection, ping);
                    }

                    lock (_sync)
                    {
                        var cutoff = DateTime.Now - _options.KeepLeftMembers;
                        foreach (var gone in _members.Values.Where(m => m.LeftAt < cutoff).ToList()) _members.Remove(gone.Id);
                    }
                    PublishRoster(); // Статус «в игре» зависит от трафика туннеля, а не от событий
                }
            }
            catch (OperationCanceledException)
            {
                // Комната закрыта
            }
        }

        private async Task KeepMappingAliveAsync()
        {
            // NAT забывает неиспользуемый UDP-маппинг, и внешний адрес комнаты сменился бы, пока к ней никто не подключился
            if (_stunServer == null) return;
            try
            {
                while (true)
                {
                    await Task.Delay(MappingKeepAliveInterval, _cts.Token);
                    var mapped = await StunClient.GetMappedEndPointAsync(_peer, _stunServer, _cts.Token);
                    if (mapped != null && !mapped.Equals(PublicEndPoint))
                    {
                        Trace.WriteLine($"[Core.Rooms.Host] Public endpoint changed {PublicEndPoint} -> {mapped}");
                        PublicEndPoint = mapped;
                        Raise(PublicEndPointChanged);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Комната закрыта
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Рассылает состав комнаты, если он изменился с прошлого раза.</summary>
        private void PublishRoster()
        {
            byte[] roster;
            List<Connection> targets;
            lock (_sync)
            {
                foreach (var member in _members.Values) member.RefreshStatus();
                roster = RoomProtocol.BuildRoster(SnapshotMembers());
                if (_lastRoster != null && roster.AsSpan().SequenceEqual(_lastRoster)) return;
                _lastRoster = roster;
                targets = _connections.Values.Where(c => c.MemberId != null).ToList();
            }

            foreach (var connection in targets) _ = TrySendAsync(connection, roster);
            Raise(MembersChanged);
        }

        private List<RoomMember> SnapshotMembers()
        {
            var list = new List<RoomMember> { new(HostMemberId, HostName, RoomMemberStatus.Host, null) };
            list.AddRange(_members.Values.OrderBy(m => m.Id).Select(m => m.ToMember()));
            return list;
        }

        private async Task BroadcastAsync(byte[] frame)
        {
            var targets = _connections.Values.Where(c => c.MemberId != null).ToList();
            await Task.WhenAll(targets.Select(c => TrySendAsync(c, frame)));
        }

        private static async Task TrySendAsync(Connection connection, byte[] frame)
        {
            try
            {
                await connection.Channel.SendDataAsync(frame);
            }
            catch (InvalidOperationException)
            {
                // Канал закрыт — участник уже уходит
            }
        }

        private static void TrySendDatagram(Connection connection, byte[] frame)
        {
            try
            {
                _ = connection.Channel.SendDatagramAsync(frame);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void Raise(EventHandler? handler)
        {
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Trace.WriteLine($"[Core.Rooms.Host] Event handler threw exception: {ex}"); }
        }

        private void Raise<T>(EventHandler<T>? handler, T args)
        {
            if (handler == null) return;
            try { handler(this, args); }
            catch (Exception ex) { Trace.WriteLine($"[Core.Rooms.Host] Event handler threw exception: {ex}"); }
        }

        // Сначала адреса сетей с основным шлюзом (домашний Wi-Fi, кабель): у виртуальных адаптеров Hyper-V
        // и VirtualBox шлюза обычно нет, и их адрес другу в той же сети не поможет
        internal static IEnumerable<IPAddress> LocalIPv4Addresses()
        {
            var found = new List<(IPAddress Address, bool HasGateway)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = nic.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                      !g.Address.Equals(IPAddress.Any));
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        found.Add((address.Address, hasGateway));
                }
            }
            return found.OrderByDescending(a => a.HasGateway).Select(a => a.Address);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            _cts.Cancel();
            _peer.DataReceived -= OnRawDataReceived;
            foreach (var connection in _connections.Values) connection.Dispose();
            _connections.Clear();
            _peer.Dispose();
            Trace.WriteLine($"[Core.Rooms.Host] Room on port {Port} closed");
        }

        // Состояние участника на стороне хоста; меняется под _sync
        private sealed class MemberState
        {
            private const double PingSmoothing = 0.3;

            public MemberState(int id, string name, Connection connection)
            {
                Id = id;
                Name = name;
                Connection = connection;
                Status = RoomMemberStatus.Joining;
            }

            public int Id { get; }
            public string Name { get; }
            public Connection? Connection { get; private set; }
            public RoomMemberStatus Status { get; private set; }
            public double? PingMs { get; private set; }
            public DateTime? LeftAt { get; private set; }

            /// <summary>Сглаженный пинг; true — это первый замер.</summary>
            public bool UpdatePing(int rttMs)
            {
                bool first = PingMs == null;
                PingMs = PingMs is double old ? old + PingSmoothing * (rttMs - old) : rttMs;
                return first;
            }

            public void RefreshStatus()
            {
                if (Connection == null || PingMs == null) return; // Вышел или ещё не измерен
                Status = Connection.Tunnel.ForwardedConnectionCount > 0 ? RoomMemberStatus.InGame : RoomMemberStatus.InRoom;
            }

            public void MarkLeft()
            {
                Connection = null;
                Status = RoomMemberStatus.Left;
                PingMs = null;
                LeftAt = DateTime.Now;
            }

            public RoomMember ToMember() => new(Id, Name, Status, PingMs is double p ? (int)Math.Round(p) : null, LeftAt);
        }

        // Канал с одним игроком и его туннель к серверам хоста
        private sealed class Connection : IDisposable
        {
            private readonly RoomHost _host;

            public Connection(RoomHost host, IPEndPoint endPoint)
            {
                _host = host;
                EndPoint = endPoint;
                Channel = new ReliableChannel(host._peer);
                Tunnel = new TunnelSession(Channel);
                Channel.DataReceived += OnData;
                Channel.DatagramReceived += OnDatagram;
                Channel.Closed += OnClosed;
            }

            public IPEndPoint EndPoint { get; }
            public ReliableChannel Channel { get; }
            public TunnelSession Tunnel { get; private set; }

            /// <summary>Id участника после Join; null — ещё не представился.</summary>
            public int? MemberId { get; set; }

            /// <summary>Игрок переподключился и начал нумерацию соединений заново — старые пересылки больше не нужны.</summary>
            public void ResetTunnel()
            {
                Tunnel.Dispose();
                Tunnel = new TunnelSession(Channel);
            }

            private void OnData(object? sender, byte[] frame) => _host.OnFrame(this, frame);
            private void OnDatagram(object? sender, byte[] frame) => _host.OnDatagram(this, frame);
            private void OnClosed(object? sender, EventArgs e) => _host.OnChannelClosed(this);

            public void Dispose()
            {
                Channel.Closed -= OnClosed;
                Tunnel.Dispose();
                Channel.Dispose();
            }
        }
    }
}
