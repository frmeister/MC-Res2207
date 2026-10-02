using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace MC_Ref2207_NetSocketLib
{
    public class ReliableChannel : IDisposable
    {
        // -- Конфигурация --
        private const int MaxRetries = 5;
        private const int RetryDelayMs = 1000; // Интервал Hello при рукопожатии
        private const int InactivityTimeoutMs = 30000;
        private const int KeepAliveIntervalMs = 5000; // Чаще таймаута неактивности и времени жизни UDP-маппинга в типичном NAT

        // Отправка окном: до SendWindowSize пакетов в пути без подтверждения. Ждать ACK каждого пакета
        // (как раньше) — это один пакет за время туда-обратно, для Minecraft на порядки медленнее нужного.
        private const int SendWindowSize = 64;
        private const int SendQueueCapacity = 256; // Очередь полна — SendDataAsync ждёт, и источник данных притормаживает
        private const int MaxOutOfOrderPackets = SendWindowSize * 4; // Пакеты дальше по номеру — мусор, не буферизуем
        private const int InitialRtoMs = 1000; // Таймаут повтора до первого замера RTT (RFC 6298)
        private const int MinRtoMs = 200;
        private const int MaxRtoMs = 3000;
        private const int RetransmitCheckIntervalMs = 20;
        private const int DeliveryTimeoutMs = 10000; // Пакет не подтверждён дольше — партнёр считается потерянным

        /// <summary>
        /// Размер данных, который помещается в один IP-пакет почти на любом пути в интернете (как минимальный размер в QUIC).
        /// Большие сообщения тоже доставляются, но IP режет их на фрагменты, и потеря любого фрагмента теряет весь пакет.
        /// </summary>
        public const int SafePayloadSize = 1200;

        // -- Состояния и зависимости --
        private volatile ConnectionState _state = ConnectionState.Disconnected;
        private volatile TaskCompletionSource<bool>? _handshakeTcs; // Сигнал от HandleHelloAck о завершении рукопожатия
        private readonly object _stateLock = new object(); // Объект для синхронизации
        private readonly UdpPeer _udpPeer;
        private IPEndPoint? _remoteEndPoint;
        private readonly Timer _keepAliveTimer;
        private long _lastReceivedTicks; // Environment.TickCount64 последнего корректного пакета от партнёра
        private int _sessionId;
        private int _peerSessionId;
        private readonly CancellationTokenSource _closeCts = new();

        // -- Для отправки --
        private int _nextSendSeq = 0; // Следующий номер последовательности для отправки
        private readonly ConcurrentDictionary<int, OutgoingPacket> _sentPackets = new(); // Отправленные, но ещё не подтверждённые пакеты
        private readonly Channel<byte[]> _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity) { SingleReader = true }); // Очередь данных для отправки
        private readonly SemaphoreSlim _windowSlots = new(SendWindowSize, SendWindowSize); // Свободные места в окне отправки
        private readonly object _rttLock = new object();
        private double _smoothedRttMs;
        private double _rttVariationMs;
        private bool _hasRttSample;
        private int _rtoMs = InitialRtoMs;

        // -- Для получения --
        private int _expectedRecvSeq = 0; // Ожидаемый номер последовательности для получения
        private readonly SortedList<int, byte[]> _receivedBuffer = new(); // Буфер для пакетов, пришедших не по порядку
        private readonly Channel<byte[]> _receiveQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true }); // Очередь для полученных данных

        // -- События --
        private EventHandler<byte[]>? _dataReceived;
        private int _dispatchStarted;

        // Полученные данные выдаются либо через это событие, либо через ReceiveDataAsync — не одновременно.
        // Доставка в событие стартует при первой подписке, поэтому данные, пришедшие до неё, не теряются.
        public event EventHandler<byte[]>? DataReceived
        {
            add
            {
                lock (_stateLock) { _dataReceived += value; }
                if (Interlocked.Exchange(ref _dispatchStarted, 1) == 0)
                {
                    _ = Task.Run(ProcessReceiveQueueAsync);
                }
            }
            remove
            {
                lock (_stateLock) { _dataReceived -= value; }
            }
        }

        /// <summary>
        /// Датаграмма без гарантии доставки и порядка (SendDatagramAsync партнёра).
        /// Вызывается прямо в потоке приёма UDP — обработчик должен быть быстрым и не блокирующим.
        /// </summary>
        public event EventHandler<byte[]>? DatagramReceived;

        /// <summary>
        /// Соединение закрыто: вызовом Close(), по неактивности партнёра или потому что данные не удалось доставить.
        /// </summary>
        public event EventHandler? Closed;

        public ReliableChannel(UdpPeer udpPeer)
        {
            _udpPeer = udpPeer ?? throw new ArgumentNullException(nameof(udpPeer));
            // Подписались на событие получения данных
            _udpPeer.DataReceived += OnUdpDataReceived;

            // Таймер keep-alive и проверки неактивности, запускается после установки соединения
            _keepAliveTimer = new Timer(CheckConnection, null, Timeout.Infinite, Timeout.Infinite);

            // Фоновая отправка и повтор потерянных пакетов работают до Close()
            Task.Run(ProcessSendQueueAsync);
            Task.Run(RetransmitLoopAsync);
        }

        public ConnectionState State => _state;

        // -- Управление соединением --
        // Рукопожатие симметричное, ConnectAsync вызывают обе стороны:
        // пока идёт подключение, каждая сторона раз в RetryDelayMs шлёт Hello со своим sessionId.
        // На каждый Hello отвечаем HelloAck = [наш sessionId][sessionId из Hello]. Получив HelloAck со своим
        // sessionId, сторона знает, что связь работает в обе стороны, и переходит в Established.
        // На HelloAck не отвечаем, поэтому после рукопожатия пакеты рукопожатия больше не ходят.
        public async Task<bool> ConnectAsync(IPEndPoint remoteEndPoint, int handshakeTimeoutMs = -1, CancellationToken cancellationToken = default)
        {
            if (remoteEndPoint == null) throw new ArgumentNullException(nameof(remoteEndPoint));

            var handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_stateLock)
            {
                if (_state != ConnectionState.Disconnected)
                {
                    throw new InvalidOperationException($"Cannot connect while in state: {_state}");
                }

                _remoteEndPoint = remoteEndPoint;
                if (_sessionId == 0)
                {
                    _sessionId = Random.Shared.Next(1, int.MaxValue); // 0 зарезервирован под "сессия неизвестна"
                }
                _handshakeTcs = handshakeTcs;
                _state = ConnectionState.Connecting;
            }

            Trace.WriteLine($"[Core.Network.ReliableChannel] ConnectAsync: starting handshake with {_remoteEndPoint}, session={_sessionId}");

            // Determine handshake timeout: if caller provided value (>0) use it, otherwise use default MaxRetries * RetryDelayMs
            int effectiveTimeoutMs = handshakeTimeoutMs > 0 ? handshakeTimeoutMs : (MaxRetries * RetryDelayMs);
            Trace.WriteLine($"[Core.Network.ReliableChannel] ConnectAsync: handshake timeout set to {effectiveTimeoutMs}ms");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMs)); // Таймаут на рукопожатие

            // Sender task: periodically send Hello until handshake completes or timeout/cancellation
            var senderTask = Task.Run(async () =>
            {
                int attempt = 0;
                while (!handshakeTcs.Task.IsCompleted && !timeoutCts.IsCancellationRequested)
                {
                    attempt++;
                    Trace.WriteLine($"[Core.Network.ReliableChannel] ConnectAsync: sending Hello attempt {attempt}");
                    await SendHelloAsync();
                    try
                    {
                        await Task.Delay(RetryDelayMs, timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Trace.WriteLine("[Core.Network.ReliableChannel] ConnectAsync: senderTask canceled due to timeout/cancellation");
                        break;
                    }
                }

                Trace.WriteLine("[Core.Network.ReliableChannel] ConnectAsync: senderTask finished");
            });

            bool success;
            // If timeout occurs, set handshake result to false. Also observe external cancellation.
            using (timeoutCts.Token.Register(() =>
            {
                Trace.WriteLine("[Core.Network.ReliableChannel] ConnectAsync: handshake timeout/cancellation triggered");
                handshakeTcs.TrySetResult(false);
            }))
            {
                success = await handshakeTcs.Task;
            }

            lock (_stateLock)
            {
                // Close() мог быть вызван во время рукопожатия — тогда Closed не перетираем
                if (_state == ConnectionState.Connecting)
                {
                    _state = success ? ConnectionState.Established : ConnectionState.Disconnected;
                }
                success = _state == ConnectionState.Established;
                _handshakeTcs = null;

                if (success)
                {
                    Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
                    _keepAliveTimer.Change(KeepAliveIntervalMs, KeepAliveIntervalMs);
                }
            }

            if (success)
            {
                Trace.WriteLine("[Core.Network.ReliableChannel] Handshake succeeded; connection established.");
            }
            else
            {
                Trace.WriteLine("[Core.Network.ReliableChannel] Handshake failed or canceled, reverting to Disconnected.");
            }

            // Останавливаем рассылку Hello сразу, не дожидаясь очередной паузы RetryDelayMs
            timeoutCts.Cancel();
            await senderTask;

            return success;
        }

        // -- Отправка данных --
        // Данные доставляются надёжно и по порядку. Метод ставит их в очередь и не ждёт подтверждения доставки,
        // но если очередь заполнена (сеть не успевает), ждёт освобождения места.
        // Для передачи через интернет лучше не больше SafePayloadSize байт за раз.
        public async Task SendDataAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > Packet.MaxPayloadSize)
            {
                throw new ArgumentException($"Data is larger than {Packet.MaxPayloadSize} bytes and does not fit into one packet.", nameof(data));
            }
            if (_state != ConnectionState.Established)
            {
                throw new InvalidOperationException($"Cannot send data while in state: {_state}");
            }

            try
            {
                // Копируем: вызывающий код может переиспользовать массив, пока пакет ещё в очереди
                await _sendQueue.Writer.WriteAsync((byte[])data.Clone(), cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException($"Cannot send data while in state: {_state}");
            }
        }

        // -- Отправка датаграмм --
        // Без гарантии доставки и порядка, сразу в сеть, мимо очереди надёжных данных.
        // Подходит для пересылки трафика UDP-игр: они сами повторяют потерянное, а повторы туннеля только добавили бы задержку.
        public Task SendDatagramAsync(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > Packet.MaxPayloadSize)
            {
                throw new ArgumentException($"Data is larger than {Packet.MaxPayloadSize} bytes and does not fit into one packet.", nameof(data));
            }
            if (_state != ConnectionState.Established)
            {
                throw new InvalidOperationException($"Cannot send data while in state: {_state}");
            }

            return SendRawAsync(new Packet(data, 0, 0, PacketType.Datagram).ToBytes(), PacketType.Datagram);
        }

        private async Task ProcessSendQueueAsync()
        {
            var closeToken = _closeCts.Token;
            try
            {
                await foreach (var dataToSend in _sendQueue.Reader.ReadAllAsync(closeToken))
                {
                    // Не больше SendWindowSize неподтверждённых пакетов: место освобождает HandleAck
                    await _windowSlots.WaitAsync(closeToken);
                    if (_state != ConnectionState.Established)
                    {
                        _windowSlots.Release();
                        return;
                    }

                    var packet = new Packet(dataToSend, Interlocked.Increment(ref _nextSendSeq) - 1); // Увеличиваем seq затем используем
                    var outgoingPacket = new OutgoingPacket(packet.Sequence, packet.ToBytes(), Environment.TickCount64, CurrentRtoMs);
                    _sentPackets[packet.Sequence] = outgoingPacket;

                    await SendRawAsync(outgoingPacket.Bytes, PacketType.Data);
                }
            }
            catch (OperationCanceledException)
            {
                // Close() — выходим
            }
        }

        // Повторяет пакеты, на которые ACK не пришёл за RTO. Для каждого следующего повтора пакета интервал удваивается.
        private async Task RetransmitLoopAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RetransmitCheckIntervalMs));
            try
            {
                while (await timer.WaitForNextTickAsync(_closeCts.Token))
                {
                    if (_state != ConnectionState.Established) continue;

                    long now = Environment.TickCount64;
                    foreach (var outgoingPacket in _sentPackets.Values)
                    {
                        bool resend = false;
                        bool giveUp = false;
                        int attempt = 0;
                        lock (outgoingPacket)
                        {
                            if (now - outgoingPacket.FirstSentTicks > DeliveryTimeoutMs)
                            {
                                giveUp = true;
                            }
                            else if (now - outgoingPacket.LastSentTicks >= outgoingPacket.RtoMs)
                            {
                                resend = true;
                                outgoingPacket.LastSentTicks = now;
                                outgoingPacket.Attempts++;
                                outgoingPacket.RtoMs = Math.Min(outgoingPacket.RtoMs * 2, MaxRtoMs);
                                attempt = outgoingPacket.Attempts;
                            }
                        }

                        if (giveUp)
                        {
                            // Получатель выдаёт данные строго по порядку и без этого пакета никогда не отдаст следующие,
                            // поэтому продолжать нельзя — закрываем соединение
                            Trace.WriteLine($"[Core.Network.ReliableChannel] Packet seq={outgoingPacket.Sequence} not confirmed for {DeliveryTimeoutMs}ms. Closing connection.");
                            Close();
                            return;
                        }

                        if (resend && _sentPackets.ContainsKey(outgoingPacket.Sequence))
                        {
                            Trace.WriteLine($"[Core.Network.ReliableChannel] Retransmitting seq={outgoingPacket.Sequence}, attempt {attempt}");
                            await SendRawAsync(outgoingPacket.Bytes, PacketType.Data);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Close() — выходим
            }
        }

        private int CurrentRtoMs
        {
            get { lock (_rttLock) { return _rtoMs; } }
        }

        // Таймаут повтора по замерам RTT, как в TCP (RFC 6298)
        private void UpdateRto(long rttMs)
        {
            lock (_rttLock)
            {
                if (!_hasRttSample)
                {
                    _smoothedRttMs = rttMs;
                    _rttVariationMs = rttMs / 2.0;
                    _hasRttSample = true;
                }
                else
                {
                    _rttVariationMs = 0.75 * _rttVariationMs + 0.25 * Math.Abs(_smoothedRttMs - rttMs);
                    _smoothedRttMs = 0.875 * _smoothedRttMs + 0.125 * rttMs;
                }
                _rtoMs = (int)Math.Clamp(_smoothedRttMs + 4 * _rttVariationMs, MinRtoMs, MaxRtoMs);
            }
        }

        private Task SendHelloAsync()
        {
            var payload = BitConverter.GetBytes(_sessionId);
            return SendRawAsync(new Packet(payload, 0, 0, PacketType.Hello).ToBytes(), PacketType.Hello);
        }

        private async Task SendRawAsync(byte[] bytes, PacketType type)
        {
            var remoteEndPoint = _remoteEndPoint;
            if (remoteEndPoint == null) return;

            try
            {
                await _udpPeer.SendToAsync(bytes, remoteEndPoint);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] Failed to send {type} packet, Error: {ex.Message}");
            }
        }

        // -- Получение данных --
        private void OnUdpDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
        {
            var state = _state;
            if (state == ConnectionState.Disconnected || state == ConnectionState.Closed) return; // Игнорируем если не установлено соединение
            if (_remoteEndPoint != null && !e.RemoteEndPoint.Equals(_remoteEndPoint)) return; // Игнорируем если не от нашего партнера

            try
            {
                // Отдельные пакеты данных не логируем: при пересылке игрового трафика их сотни в секунду
                var packet = Packet.FromBytes(e.Data);

                // Активностью считаем только корректные пакеты
                Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);

                switch(packet.Type)
                {
                    case PacketType.Ack:
                        HandleAck(packet.Acknowledgment);
                        break;
                    case PacketType.Data:
                        HandleData(packet);
                        break;
                    case PacketType.Hello:
                        HandleHello(packet);
                        break;
                    case PacketType.HelloAck:
                        HandleHelloAck(packet);
                        break;
                    case PacketType.KeepAlive:
                        // Достаточно обновления времени активности выше
                        break;
                    case PacketType.Datagram:
                        HandleDatagram(packet);
                        break;
                    default:
                        Trace.WriteLine($"[Core.Network.ReliableChannel] Unknown packet type: {packet.Type}");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] Invalid packet received: {ex.Message}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] Error processing received packet: {ex.Message}");
            }
        }

        private void HandleAck(int ackSeq)
        {
            // Нет в словаре — ACK на повтор уже подтверждённого пакета, ничего не делаем
            if (!_sentPackets.TryRemove(ackSeq, out var outgoingPacket)) return;

            int attempts;
            long lastSentTicks;
            lock (outgoingPacket)
            {
                attempts = outgoingPacket.Attempts;
                lastSentTicks = outgoingPacket.LastSentTicks;
            }

            // RTT меряем только по пакетам без повторов: иначе неясно, на какую из отправок пришёл ACK (алгоритм Карна)
            if (attempts == 1)
            {
                UpdateRto(Environment.TickCount64 - lastSentTicks);
            }
            _windowSlots.Release(); // Место в окне освободилось
        }

        private void HandleDatagram(Packet packet)
        {
            try
            {
                DatagramReceived?.Invoke(this, packet.Payload);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] DatagramReceived handler threw exception: {ex}");
            }
        }

        private void HandleData(Packet packet)
        {
            // Так далеко вперёд честный отправитель уйти не может (окно меньше) — не буферизуем и не подтверждаем
            if (packet.Sequence >= _expectedRecvSeq + MaxOutOfOrderPackets) return;

            // Отправляем ACK для полученного пакета
            SendAck(packet.Sequence);

            if (packet.Sequence == _expectedRecvSeq)
            {
                // Получен ожидаемый пакет
                EnqueueReceivedData(packet.Payload);
                _expectedRecvSeq++;

                // Проверяем буфер на наличие следующих по порядку пакетов
                while (_receivedBuffer.TryGetValue(_expectedRecvSeq, out var bufferedData))
                {
                    _receivedBuffer.Remove(_expectedRecvSeq);
                    EnqueueReceivedData(bufferedData);
                    _expectedRecvSeq++;
                }
            }
            else if (packet.Sequence > _expectedRecvSeq)
            {
                // Получен пакет с более высоким номером (предыдущий потерялся) — ждём повтора пропущенного
                _receivedBuffer[packet.Sequence] = packet.Payload;
            }
            // else: packet.Sequence < _expectedRecvSeq -> дубликат, игнорируем (уже обработан)
        }

        private void SendAck(int seqNum)
        {
            var ackPacket = new Packet(Array.Empty<byte>(), 0, seqNum, PacketType.Ack); // Ack не содержит полезной нагрузки
            _ = SendRawAsync(ackPacket.ToBytes(), PacketType.Ack); // Не ждем завершения отправки Ack
        }

        private void EnqueueReceivedData(byte[] data)
        {
            _receiveQueue.Writer.TryWrite(data);
        }

        private async Task ProcessReceiveQueueAsync()
        {
            // Завершается, когда Close() закрывает очередь и все уже полученные данные выданы
            await foreach (var dataToProcess in _receiveQueue.Reader.ReadAllAsync())
            {
                try
                {
                    // Вызываем событие для верхнего уровня (например TcpBridge)
                    _dataReceived?.Invoke(this, dataToProcess);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Core.Network.ReliableChannel] DataReceived handler threw exception: {ex}");
                }
            }
        }

        private void HandleHello(Packet packet)
        {
            if (packet.Payload.Length < 4) return;

            int incomingSession = BitConverter.ToInt32(packet.Payload, 0);
            UpdatePeerSession(incomingSession);

            // Отвечаем на каждый Hello: наш прошлый HelloAck мог потеряться
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), _sessionId);
            BitConverter.TryWriteBytes(payload.AsSpan(4, 4), incomingSession);
            _ = SendRawAsync(new Packet(payload, 0, 0, PacketType.HelloAck).ToBytes(), PacketType.HelloAck);
        }

        private void HandleHelloAck(Packet packet)
        {
            if (packet.Payload.Length < 8) return;

            int peerSession = BitConverter.ToInt32(packet.Payload, 0);
            int echoedSession = BitConverter.ToInt32(packet.Payload, 4);
            if (echoedSession != _sessionId) return; // Ответ на чужой или устаревший Hello

            UpdatePeerSession(peerSession);
            if (_handshakeTcs?.TrySetResult(true) == true)
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] Handshake complete, peerSession={_peerSessionId}");
            }
        }

        private void UpdatePeerSession(int incomingSession)
        {
            if (_peerSessionId == incomingSession) return;

            if (_peerSessionId != 0)
            {
                // Партнёр переподключился с новой сессией — нумерация в обе стороны начинается заново
                Trace.WriteLine($"[Core.Network.ReliableChannel] Peer session changed {_peerSessionId} -> {incomingSession}, resetting sequences.");
                _expectedRecvSeq = 0;
                _receivedBuffer.Clear();
                Interlocked.Exchange(ref _nextSendSeq, 0);
                foreach (var seq in _sentPackets.Keys)
                {
                    // Старые пакеты новой сессии партнёра не нужны — прекращаем их переотправку
                    if (_sentPackets.TryRemove(seq, out _)) _windowSlots.Release();
                }
            }
            _peerSessionId = incomingSession;
        }

        // -- Чтение данных (если не используется событие DataReceived) --
        // Возвращает null, когда соединение закрыто и все полученные данные уже прочитаны.
        public async Task<byte[]?> ReceiveDataAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _dispatchStarted) != 0)
            {
                throw new InvalidOperationException("Received data is delivered through the DataReceived event.");
            }
            if (_state == ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"Cannot receive data while in state: {_state}");
            }

            while (await _receiveQueue.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_receiveQueue.Reader.TryRead(out var data)) return data;
            }
            return null;
        }

        // -- Keep-alive и проверка неактивности --
        private void CheckConnection(object? state)
        {
            if (_state != ConnectionState.Established) return;

            if (Environment.TickCount64 - Interlocked.Read(ref _lastReceivedTicks) > InactivityTimeoutMs)
            {
                Trace.WriteLine("[Core.Network.ReliableChannel] Inactivity detected, closing connection.");
                Close();
                return;
            }

            // Без трафика соединение закрылось бы по неактивности, а NAT забыл бы пробитый маппинг
            _ = SendRawAsync(new Packet(Array.Empty<byte>(), 0, 0, PacketType.KeepAlive).ToBytes(), PacketType.KeepAlive);
        }

        // -- Закрытие --
        public void Close()
        {
            lock(_stateLock)
            {
                if (_state == ConnectionState.Closed) return; // Проверяем и меняем состояние атомарно
                _state = ConnectionState.Closed;

                try { _keepAliveTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }

            _udpPeer.DataReceived -= OnUdpDataReceived;
            _handshakeTcs?.TrySetResult(false); // Прерываем ожидающий ConnectAsync
            _closeCts.Cancel(); // Останавливаем фоновую отправку
            _sendQueue.Writer.TryComplete();
            _receiveQueue.Writer.TryComplete(); // Читатели дочитают уже полученные данные и завершатся
            Trace.WriteLine("[Core.Network.ReliableChannel] Connection closed.");

            // Не в текущем потоке: Close() может вызываться из обработчиков и фоновых циклов самого канала
            var closed = Closed;
            if (closed != null)
            {
                _ = Task.Run(() =>
                {
                    try { closed(this, EventArgs.Empty); }
                    catch (Exception ex) { Trace.WriteLine($"[Core.Network.ReliableChannel] Closed handler threw exception: {ex}"); }
                });
            }
        }

        public void Dispose()
        {
            Close();
            _keepAliveTimer.Dispose();
            // _closeCts не освобождаем: фоновые задачи после Close() ещё могут обращаться к его Token
        }

        // -- Вспомогательный класс --
        // Изменяемые поля меняются под lock (outgoingPacket)
        private class OutgoingPacket
        {
            public int Sequence { get; }
            public byte[] Bytes { get; }
            public long FirstSentTicks { get; }
            public long LastSentTicks { get; set; }
            public int Attempts { get; set; } = 1;
            public int RtoMs { get; set; }

            public OutgoingPacket(int sequence, byte[] bytes, long sentTicks, int rtoMs)
            {
                Sequence = sequence;
                Bytes = bytes;
                FirstSentTicks = sentTicks;
                LastSentTicks = sentTicks;
                RtoMs = rtoMs;
            }
        }
    }
}
//