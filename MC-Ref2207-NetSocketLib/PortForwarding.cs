// MCTunnel.Core.Network/PortForwarding.cs

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;

namespace MCTunnel.Core.Network
{
    public enum ForwardProtocol : byte
    {
        Tcp = 0,
        Udp = 1,
    }

    /// <summary>
    /// Порт игрового сервера, который открывают партнёру, например UDP 28015 (Rust) или TCP 25565 (Minecraft).
    /// </summary>
    public sealed record ForwardRule(ForwardProtocol Protocol, int Port)
    {
        public override string ToString() => $"{(Protocol == ForwardProtocol.Tcp ? "TCP" : "UDP")} {Port}";

        /// <summary>
        /// Разбирает запись вида "udp:28015" или "tcp:25565".
        /// </summary>
        public static bool TryParse(string? text, [NotNullWhen(true)] out ForwardRule? rule)
        {
            rule = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Trim().Split(':');
            if (parts.Length != 2) return false;

            ForwardProtocol protocol;
            switch (parts[0].Trim().ToLowerInvariant())
            {
                case "tcp": protocol = ForwardProtocol.Tcp; break;
                case "udp": protocol = ForwardProtocol.Udp; break;
                default: return false;
            }

            if (!int.TryParse(parts[1].Trim(), out var port) || port < 1 || port > IPEndPoint.MaxPort) return false;

            rule = new ForwardRule(protocol, port);
            return true;
        }

        /// <summary>
        /// true, если на этом порту компьютера сейчас никто не слушает — например, сервер ещё не запущен.
        /// </summary>
        public bool IsLocalPortFree() => PortProbe.IsFree(Protocol, Port);
    }

    /// <summary>
    /// Порт сервера партнёра, открытый на этом компьютере: игра подключается к LocalEndPoint.
    /// </summary>
    public sealed class OpenedPort
    {
        public ForwardRule RemoteRule { get; }
        public IPEndPoint LocalEndPoint { get; }

        public OpenedPort(ForwardRule remoteRule, IPEndPoint localEndPoint)
        {
            RemoteRule = remoteRule;
            LocalEndPoint = localEndPoint;
        }
    }

    internal static class PortProbe
    {
        // Пробуем монопольно занять порт на всех адресах: получилось — на нём никто не слушает
        public static bool IsFree(ForwardProtocol protocol, int port)
        {
            using var socket = CreateSocket(protocol);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public static Socket CreateSocket(ForwardProtocol protocol) => protocol == ForwardProtocol.Tcp
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    /// <summary>
    /// Пересылка портов игровых серверов и текстовый чат поверх ReliableChannel.
    /// Сторона с сервером вызывает ShareAsync — у партнёра на 127.0.0.1 открываются свободные порты
    /// (их адреса — в RemotePortOpened), и его игра подключается к ним как к обычному локальному серверу.
    /// TCP идёт через надёжную доставку ReliableChannel, UDP — датаграммами: UDP-игры (Rust/RakNet) сами
    /// повторяют потерянное, а повторы туннеля только добавили бы задержку.
    /// Партнёр может подключиться только к портам из ShareAsync и только на этом компьютере (127.0.0.1).
    /// Обе стороны могут открывать порты одновременно.
    /// </summary>
    public sealed class TunnelSession : IDisposable
    {
        // Первый байт каждого сообщения — тип кадра
        private const byte TextFrame = 0x01;         // [текст UTF-8]
        private const byte SharesFrame = 0x02;       // хост → клиент: [N] затем N × [ruleId][протокол][порт(2)]
        private const byte TcpOpenFrame = 0x10;      // клиент → хост: [streamId(4)][ruleId]
        private const byte TcpDataFrame = 0x11;      // клиент → хост: [streamId(4)][данные]
        private const byte TcpCloseFrame = 0x12;     // клиент → хост: [streamId(4)]
        private const byte TcpDataBackFrame = 0x13;  // хост → клиент: [streamId(4)][данные]
        private const byte TcpCloseBackFrame = 0x14; // хост → клиент: [streamId(4)]
        private const byte UdpFrame = 0x20;          // клиент → хост, датаграммой: [ruleId][flowId(2)][данные]
        private const byte UdpBackFrame = 0x21;      // хост → клиент, датаграммой: [ruleId][flowId(2)][данные]

        private const int TcpHeaderSize = 5;
        private const int UdpHeaderSize = 4;
        private const int TcpChunkSize = ReliableChannel.SafePayloadSize - TcpHeaderSize;
        private const int UdpBufferSize = Packet.MaxPayloadSize - UdpHeaderSize;
        private const int UdpFlowIdleTimeoutMs = 60000;
        private static readonly TimeSpan TcpCloseTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

        private readonly ReliableChannel _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _sync = new object();
        private int _disposed;

        // Сторона с сервером (хост)
        private readonly Dictionary<byte, ForwardRule> _sharedRules = new(); // под _sync
        private readonly ConcurrentDictionary<int, TcpBridge> _hostStreams = new();
        private readonly ConcurrentDictionary<(byte RuleId, ushort FlowId), HostUdpFlow> _hostUdpFlows = new();

        // Сторона с игрой (клиент)
        private readonly Dictionary<byte, OpenedPort> _remotePorts = new(); // под _sync
        private readonly List<Socket> _listeners = new(); // под _sync
        private readonly ConcurrentDictionary<int, TcpBridge> _clientStreams = new();
        private readonly ConcurrentDictionary<byte, ClientUdpPort> _clientUdpPorts = new();
        private int _nextStreamId;

        /// <summary>Сообщение чата от партнёра.</summary>
        public event EventHandler<string>? TextReceived;

        /// <summary>Партнёр открыл порт, и он доступен на этом компьютере.</summary>
        public event EventHandler<OpenedPort>? RemotePortOpened;

        public TunnelSession(ReliableChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _channel.DataReceived += OnFrameReceived;
            _channel.DatagramReceived += OnDatagramReceived;
            _channel.Closed += OnChannelClosed;
            _ = CleanupIdleUdpFlowsAsync();
        }

        /// <summary>Порты партнёра, открытые на этом компьютере.</summary>
        public IReadOnlyList<OpenedPort> RemotePorts
        {
            get { lock (_sync) { return _remotePorts.Values.ToList(); } }
        }

        public Task SendTextAsync(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var bytes = Encoding.UTF8.GetBytes(text);
            var frame = new byte[1 + bytes.Length];
            frame[0] = TextFrame;
            bytes.CopyTo(frame, 1);
            return _channel.SendDataAsync(frame);
        }

        /// <summary>
        /// Открывает партнёру порты серверов этого компьютера. Можно вызывать повторно, чтобы добавить порты.
        /// </summary>
        public Task ShareAsync(IEnumerable<ForwardRule> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            byte[] frame;
            lock (_sync)
            {
                foreach (var rule in rules)
                {
                    if (_sharedRules.ContainsValue(rule)) continue;
                    if (_sharedRules.Count == byte.MaxValue) throw new ArgumentException("Too many shared ports.", nameof(rules));
                    _sharedRules[(byte)(_sharedRules.Count + 1)] = rule;
                }

                frame = new byte[2 + _sharedRules.Count * 4];
                frame[0] = SharesFrame;
                frame[1] = (byte)_sharedRules.Count;
                int offset = 2;
                foreach (var (ruleId, rule) in _sharedRules)
                {
                    frame[offset] = ruleId;
                    frame[offset + 1] = (byte)rule.Protocol;
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), (ushort)rule.Port);
                    offset += 4;
                }
            }

            Trace.WriteLine($"[Core.Network.Tunnel] Sharing ports: {string.Join(", ", rules)}");
            return _channel.SendDataAsync(frame);
        }

        // -- Приём кадров --
        // Надёжные кадры приходят по одному и по порядку из цикла доставки ReliableChannel
        private void OnFrameReceived(object? sender, byte[] frame)
        {
            if (frame.Length == 0) return;

            try
            {
                switch (frame[0])
                {
                    case TextFrame:
                        TextReceived?.Invoke(this, Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
                        break;
                    case SharesFrame:
                        HandleShares(frame);
                        break;
                    case TcpOpenFrame when frame.Length >= TcpHeaderSize + 1:
                        HandleTcpOpen(ReadStreamId(frame), frame[TcpHeaderSize]);
                        break;
                    case TcpDataFrame when frame.Length >= TcpHeaderSize:
                        {
                            if (_hostStreams.TryGetValue(ReadStreamId(frame), out var stream)) stream.EnqueueWrite(frame.AsSpan(TcpHeaderSize).ToArray());
                            break;
                        }
                    case TcpCloseFrame when frame.Length >= TcpHeaderSize:
                        {
                            if (_hostStreams.TryGetValue(ReadStreamId(frame), out var stream)) stream.OnRemoteClosed();
                            break;
                        }
                    case TcpDataBackFrame when frame.Length >= TcpHeaderSize:
                        {
                            if (_clientStreams.TryGetValue(ReadStreamId(frame), out var stream)) stream.EnqueueWrite(frame.AsSpan(TcpHeaderSize).ToArray());
                            break;
                        }
                    case TcpCloseBackFrame when frame.Length >= TcpHeaderSize:
                        {
                            if (_clientStreams.TryGetValue(ReadStreamId(frame), out var stream)) stream.OnRemoteClosed();
                            break;
                        }
                    default:
                        Trace.WriteLine($"[Core.Network.Tunnel] Unknown or malformed frame 0x{frame[0]:X2}, length {frame.Length}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Core.Network.Tunnel] Error handling frame 0x{frame[0]:X2}: {ex}");
            }
        }

        // Датаграммы приходят прямо из потока приёма UDP — здесь ничего не ждём
        private void OnDatagramReceived(object? sender, byte[] frame)
        {
            if (frame.Length < UdpHeaderSize) return;

            byte ruleId = frame[1];
            ushort flowId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            var data = frame.AsSpan(UdpHeaderSize).ToArray();

            switch (frame[0])
            {
                case UdpFrame:
                    ForwardToLocalServer(ruleId, flowId, data);
                    break;
                case UdpBackFrame:
                    ForwardToLocalGame(ruleId, flowId, data);
                    break;
            }
        }

        private void OnChannelClosed(object? sender, EventArgs e)
        {
            Trace.WriteLine("[Core.Network.Tunnel] Channel closed, closing forwarded connections.");
            Dispose();
        }

        // -- Клиент: открываем у себя порты партнёра --
        private void HandleShares(byte[] frame)
        {
            if (frame.Length < 2) return;

            int count = frame[1];
            for (int i = 0; i < count; i++)
            {
                int offset = 2 + i * 4;
                if (offset + 4 > frame.Length) break;

                byte ruleId = frame[offset];
                var protocol = (ForwardProtocol)frame[offset + 1];
                int port = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset + 2));
                if ((protocol != ForwardProtocol.Tcp && protocol != ForwardProtocol.Udp) || port == 0) continue;

                lock (_sync)
                {
                    if (Volatile.Read(ref _disposed) != 0 || _remotePorts.ContainsKey(ruleId)) continue;
                }

                var rule = new ForwardRule(protocol, port);
                try
                {
                    var opened = protocol == ForwardProtocol.Tcp ? OpenClientTcpPort(ruleId, rule) : OpenClientUdpPort(ruleId, rule);
                    lock (_sync) { _remotePorts[ruleId] = opened; }
                    Trace.WriteLine($"[Core.Network.Tunnel] Partner shared {rule}, available at {opened.LocalEndPoint}");
                    RemotePortOpened?.Invoke(this, opened);
                }
                catch (SocketException ex)
                {
                    Trace.WriteLine($"[Core.Network.Tunnel] Cannot open local port for partner's {rule}: {ex.Message}");
                }
            }
        }

        // Порт выбирает система, а не берём номер порта сервера партнёра. Иначе, если обе копии программы на одном
        // компьютере, а сервер не запущен, хост подключался бы к "своему серверу" 127.0.0.1:порт и попадал в этот же
        // слушатель — соединения бесконечно ходили бы по кругу через туннель.
        // Слушаем только 127.0.0.1: из локальной сети туннель не виден
        private static Socket BindLocal(ForwardProtocol protocol)
        {
            var socket = PortProbe.CreateSocket(protocol);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        private OpenedPort OpenClientTcpPort(byte ruleId, ForwardRule rule)
        {
            var listener = BindLocal(ForwardProtocol.Tcp);
            listener.Listen(16);
            lock (_sync) { _listeners.Add(listener); }
            _ = AcceptLoopAsync(listener, ruleId);
            return new OpenedPort(rule, (IPEndPoint)listener.LocalEndPoint!);
        }

        private async Task AcceptLoopAsync(Socket listener, byte ruleId)
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    break;
                }

                socket.NoDelay = true; // Игры шлют мелкие пакеты, задержка Нейгла им вредит
                int streamId = Interlocked.Increment(ref _nextStreamId);
                var stream = new TcpBridge(this, streamId, TcpDataFrame, TcpCloseFrame, _clientStreams);
                _clientStreams[streamId] = stream;

                var open = new byte[TcpHeaderSize + 1];
                open[0] = TcpOpenFrame;
                WriteStreamId(open, streamId);
                open[TcpHeaderSize] = ruleId;

                // Open уйдёт раньше данных этого соединения: всё идёт через одну упорядоченную очередь ReliableChannel
                if (!await TrySendFrameAsync(open))
                {
                    socket.Dispose();
                    stream.Dispose();
                    break;
                }

                Trace.WriteLine($"[Core.Network.Tunnel] TCP stream {streamId}: local connection from {socket.RemoteEndPoint}");
                stream.Start(socket);
            }
        }

        private OpenedPort OpenClientUdpPort(byte ruleId, ForwardRule rule)
        {
            var socket = BindLocal(ForwardProtocol.Udp);
            UdpPeer.DisableConnectionResetErrors(socket);
            var port = new ClientUdpPort(ruleId, socket);
            _clientUdpPorts[ruleId] = port;
            lock (_sync) { _listeners.Add(socket); }
            _ = ClientUdpReceiveLoopAsync(port);
            return new OpenedPort(rule, (IPEndPoint)socket.LocalEndPoint!);
        }

        private async Task ClientUdpReceiveLoopAsync(ClientUdpPort port)
        {
            var buffer = new byte[UdpBufferSize];
            EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await port.Socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, _cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (_cts.IsCancellationRequested) break;
                    Trace.WriteLine($"[Core.Network.Tunnel] Local UDP receive error: {ex.Message}");
                    continue;
                }

                // Каждый адрес игры на этом компьютере — отдельный поток (flow), у хоста ему соответствует свой сокет
                ushort flowId = port.GetFlowId((IPEndPoint)result.RemoteEndPoint);
                var frame = BuildUdpFrame(UdpFrame, port.RuleId, flowId, buffer.AsSpan(0, result.ReceivedBytes));
                if (!TrySendDatagram(frame)) break;
            }
        }

        private void ForwardToLocalGame(byte ruleId, ushort flowId, byte[] data)
        {
            if (!_clientUdpPorts.TryGetValue(ruleId, out var port)) return;
            if (!port.EndPointByFlow.TryGetValue(flowId, out var flow)) return;

            flow.Touch();
            _ = SendUdpSafeAsync(port.Socket, data, flow.EndPoint);
        }

        // -- Хост: подключаемся к своему серверу от имени игры партнёра --
        private void HandleTcpOpen(int streamId, byte ruleId)
        {
            ForwardRule? rule;
            lock (_sync) { _sharedRules.TryGetValue(ruleId, out rule); }

            var stream = new TcpBridge(this, streamId, TcpDataBackFrame, TcpCloseBackFrame, _hostStreams);
            if (!_hostStreams.TryAdd(streamId, stream)) return; // Повторный Open

            if (rule == null || rule.Protocol != ForwardProtocol.Tcp)
            {
                // Партнёр может подключаться только к открытым ему портам
                Trace.WriteLine($"[Core.Network.Tunnel] TCP stream {streamId}: rule {ruleId} is not shared, refusing");
                stream.Dispose();
                return;
            }

            _ = ConnectHostStreamAsync(stream, rule);
        }

        private async Task ConnectHostStreamAsync(TcpBridge stream, ForwardRule rule)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, rule.Port), _cts.Token);
                Trace.WriteLine($"[Core.Network.Tunnel] TCP stream {stream.StreamId}: connected to 127.0.0.1:{rule.Port}");
                stream.Start(socket);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                Trace.WriteLine($"[Core.Network.Tunnel] TCP stream {stream.StreamId}: cannot connect to 127.0.0.1:{rule.Port}: {ex.Message}");
                socket.Dispose();
                stream.Dispose(); // Партнёр получит Close, и его игра увидит закрытое соединение
            }
        }

        private void ForwardToLocalServer(byte ruleId, ushort flowId, byte[] data)
        {
            if (!_hostUdpFlows.TryGetValue((ruleId, flowId), out var flow))
            {
                ForwardRule? rule;
                lock (_sync)
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    _sharedRules.TryGetValue(ruleId, out rule);
                }
                if (rule == null || rule.Protocol != ForwardProtocol.Udp) return; // Партнёр может обращаться только к открытым портам

                flow = CreateHostUdpFlow(ruleId, flowId, rule);
                if (flow == null) return;
            }

            flow.Touch();
            _ = SendUdpSafeAsync(flow.Socket, data, null);
        }

        private HostUdpFlow? CreateHostUdpFlow(byte ruleId, ushort flowId, ForwardRule rule)
        {
            // Отдельный сокет на каждого игрока партнёра: сервер видит их как разных клиентов (127.0.0.1:случайный порт)
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                UdpPeer.DisableConnectionResetErrors(socket);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.Connect(new IPEndPoint(IPAddress.Loopback, rule.Port));
            }
            catch (SocketException ex)
            {
                Trace.WriteLine($"[Core.Network.Tunnel] UDP flow {flowId}: cannot open socket to 127.0.0.1:{rule.Port}: {ex.Message}");
                socket.Dispose();
                return null;
            }

            var flow = new HostUdpFlow(ruleId, flowId, socket);
            _hostUdpFlows[(ruleId, flowId)] = flow;
            Trace.WriteLine($"[Core.Network.Tunnel] UDP flow {flowId}: forwarding to 127.0.0.1:{rule.Port} from {socket.LocalEndPoint}");
            _ = HostUdpReceiveLoopAsync(flow);
            return flow;
        }

        private async Task HostUdpReceiveLoopAsync(HostUdpFlow flow)
        {
            var buffer = new byte[UdpBufferSize];
            while (!_cts.IsCancellationRequested && !flow.IsDisposed)
            {
                int received;
                try
                {
                    received = await flow.Socket.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (flow.IsDisposed || _cts.IsCancellationRequested) break;
                    Trace.WriteLine($"[Core.Network.Tunnel] UDP flow {flow.FlowId}: receive error: {ex.Message}");
                    continue;
                }

                flow.Touch();
                if (!TrySendDatagram(BuildUdpFrame(UdpBackFrame, flow.RuleId, flow.FlowId, buffer.AsSpan(0, received)))) break;
            }
        }

        // Закрываем потоки UDP, по которым давно ничего не шло: у UDP нет "закрытия соединения"
        private async Task CleanupIdleUdpFlowsAsync()
        {
            using var timer = new PeriodicTimer(CleanupInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    long now = Environment.TickCount64;

                    foreach (var (key, flow) in _hostUdpFlows)
                    {
                        if (now - flow.LastActiveTicks > UdpFlowIdleTimeoutMs && _hostUdpFlows.TryRemove(key, out _))
                        {
                            Trace.WriteLine($"[Core.Network.Tunnel] UDP flow {flow.FlowId}: idle, closing");
                            flow.Dispose();
                        }
                    }

                    foreach (var port in _clientUdpPorts.Values)
                    {
                        foreach (var (flowId, flow) in port.EndPointByFlow)
                        {
                            if (now - flow.LastActiveTicks > UdpFlowIdleTimeoutMs && port.EndPointByFlow.TryRemove(flowId, out _))
                            {
                                port.FlowByEndPoint.TryRemove(flow.EndPoint, out _);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose()
            }
        }

        // -- Отправка --
        private async Task<bool> TrySendFrameAsync(byte[] frame)
        {
            try
            {
                await _channel.SendDataAsync(frame);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // Соединение закрыто
            }
        }

        private bool TrySendDatagram(byte[] frame)
        {
            try
            {
                _ = _channel.SendDatagramAsync(frame);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // Соединение закрыто
            }
        }

        private static async Task SendUdpSafeAsync(Socket socket, byte[] data, EndPoint? target)
        {
            try
            {
                if (target == null) await socket.SendAsync(data, SocketFlags.None);
                else await socket.SendToAsync(data, SocketFlags.None, target);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // UDP: потеря датаграммы допустима, игра повторит сама
            }
        }

        private static byte[] BuildUdpFrame(byte type, byte ruleId, ushort flowId, ReadOnlySpan<byte> data)
        {
            var frame = new byte[UdpHeaderSize + data.Length];
            frame[0] = type;
            frame[1] = ruleId;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), flowId);
            data.CopyTo(frame.AsSpan(UdpHeaderSize));
            return frame;
        }

        private static int ReadStreamId(byte[] frame) => BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1));

        private static void WriteStreamId(byte[] frame, int streamId) => BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), streamId);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _channel.DataReceived -= OnFrameReceived;
            _channel.DatagramReceived -= OnDatagramReceived;
            _channel.Closed -= OnChannelClosed;
            _cts.Cancel();

            List<Socket> listeners;
            lock (_sync)
            {
                listeners = _listeners.ToList();
                _listeners.Clear();
            }
            foreach (var listener in listeners) listener.Dispose();

            foreach (var stream in _hostStreams.Values) stream.Dispose();
            foreach (var stream in _clientStreams.Values) stream.Dispose();
            foreach (var flow in _hostUdpFlows.Values) flow.Dispose();
            _hostUdpFlows.Clear();
        }

        // Одно TCP-соединение, пересылаемое через туннель. Закрытие — кадрами Close в обе стороны:
        // локальная сторона закрылась → шлём Close и ждём Close партнёра (до него ещё могут прийти данные, бывшие в пути);
        // пришёл Close партнёра → дописываем полученное в сокет и закрываем его.
        private sealed class TcpBridge
        {
            private readonly TunnelSession _session;
            private readonly byte _dataFrame;
            private readonly byte _closeFrame;
            private readonly ConcurrentDictionary<int, TcpBridge> _table;
            private readonly Channel<byte[]> _writeQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            private NetworkStream? _stream;
            private int _closeSent;
            private int _disposed;

            public int StreamId { get; }

            public TcpBridge(TunnelSession session, int streamId, byte dataFrame, byte closeFrame, ConcurrentDictionary<int, TcpBridge> table)
            {
                _session = session;
                StreamId = streamId;
                _dataFrame = dataFrame;
                _closeFrame = closeFrame;
                _table = table;
            }

            // Данные партнёра принимаем и до подключения сокета (хост ещё подключается к серверу) — они подождут в очереди
            public void EnqueueWrite(byte[] data) => _writeQueue.Writer.TryWrite(data);

            public void OnRemoteClosed() => _writeQueue.Writer.TryComplete();

            public void Start(Socket socket)
            {
                var stream = new NetworkStream(socket, ownsSocket: true);
                _stream = stream;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    stream.Dispose();
                    return;
                }

                _ = ReadLoopAsync(stream);
                _ = WriteLoopAsync(stream);
            }

            private async Task ReadLoopAsync(NetworkStream stream)
            {
                var buffer = new byte[TcpChunkSize];
                try
                {
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read == 0) break; // Локальная сторона закрыла соединение

                        var frame = new byte[TcpHeaderSize + read];
                        frame[0] = _dataFrame;
                        WriteStreamId(frame, StreamId);
                        Buffer.BlockCopy(buffer, 0, frame, TcpHeaderSize, read);

                        // Ждёт, если сеть не успевает: чтение из сокета приостанавливается, и TCP притормаживает источник
                        if (!await _session.TrySendFrameAsync(frame)) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                {
                    // Соединение оборвано или закрыто нами
                }

                SendClose();
                // Ждём Close партнёра (после него WriteLoop закроет сокет), но не бесконечно
                _ = Task.Delay(TcpCloseTimeout).ContinueWith(_ => Dispose());
            }

            private async Task WriteLoopAsync(NetworkStream stream)
            {
                try
                {
                    await foreach (var data in _writeQueue.Reader.ReadAllAsync())
                    {
                        await stream.WriteAsync(data);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                {
                    // Локальная сторона закрыла соединение
                }

                // Партнёр закрыл соединение и всё полученное записано (или запись сломалась)
                Dispose();
            }

            private void SendClose()
            {
                if (Interlocked.Exchange(ref _closeSent, 1) != 0) return;

                var frame = new byte[TcpHeaderSize];
                frame[0] = _closeFrame;
                WriteStreamId(frame, StreamId);
                _ = _session.TrySendFrameAsync(frame);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                _table.TryRemove(new KeyValuePair<int, TcpBridge>(StreamId, this));
                _writeQueue.Writer.TryComplete();
                SendClose(); // Если закрываемся из-за ошибки — партнёр тоже должен закрыть своё соединение
                try { _stream?.Dispose(); } catch (Exception) { }
                Trace.WriteLine($"[Core.Network.Tunnel] TCP stream {StreamId} closed");
            }
        }

        private sealed class HostUdpFlow : IDisposable
        {
            private int _disposed;

            public byte RuleId { get; }
            public ushort FlowId { get; }
            public Socket Socket { get; }
            public long LastActiveTicks { get; private set; } = Environment.TickCount64;
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public HostUdpFlow(byte ruleId, ushort flowId, Socket socket)
            {
                RuleId = ruleId;
                FlowId = flowId;
                Socket = socket;
            }

            public void Touch() => LastActiveTicks = Environment.TickCount64;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Socket.Dispose();
            }
        }

        private sealed class ClientUdpPort
        {
            private int _nextFlowId;

            public byte RuleId { get; }
            public Socket Socket { get; }
            public ConcurrentDictionary<IPEndPoint, ushort> FlowByEndPoint { get; } = new();
            public ConcurrentDictionary<ushort, ClientUdpFlow> EndPointByFlow { get; } = new();

            public ClientUdpPort(byte ruleId, Socket socket)
            {
                RuleId = ruleId;
                Socket = socket;
            }

            // Вызывается только из цикла приёма этого порта
            public ushort GetFlowId(IPEndPoint endPoint)
            {
                if (FlowByEndPoint.TryGetValue(endPoint, out var flowId) && EndPointByFlow.TryGetValue(flowId, out var existing))
                {
                    existing.Touch();
                    return flowId;
                }

                flowId = (ushort)Interlocked.Increment(ref _nextFlowId);
                EndPointByFlow[flowId] = new ClientUdpFlow(endPoint);
                FlowByEndPoint[endPoint] = flowId;
                Trace.WriteLine($"[Core.Network.Tunnel] UDP flow {flowId}: local game at {endPoint}");
                return flowId;
            }
        }

        private sealed class ClientUdpFlow
        {
            public IPEndPoint EndPoint { get; }
            public long LastActiveTicks { get; private set; } = Environment.TickCount64;

            public ClientUdpFlow(IPEndPoint endPoint)
            {
                EndPoint = endPoint;
            }

            public void Touch() => LastActiveTicks = Environment.TickCount64;
        }
    }
}
