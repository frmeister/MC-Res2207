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
        private const int RetryDelayMs = 1000;
        private const int InactivityTimeoutMs = 30000;
        private const int KeepAliveIntervalMs = 5000; // Чаще таймаута неактивности и времени жизни UDP-маппинга в типичном NAT

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
        private readonly Channel<byte[]> _sendQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true }); // Очередь данных для отправки

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

        public ReliableChannel(UdpPeer udpPeer)
        {
            _udpPeer = udpPeer ?? throw new ArgumentNullException(nameof(udpPeer));
            // Подписались на событие получения данных
            _udpPeer.DataReceived += OnUdpDataReceived;

            // Таймер keep-alive и проверки неактивности, запускается после установки соединения
            _keepAliveTimer = new Timer(CheckConnection, null, Timeout.Infinite, Timeout.Infinite);

            // Фоновая отправка работает до Close()
            Task.Run(ProcessSendQueueAsync);
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
        // Данные ставятся в очередь и отправляются в фоне; метод не ждёт подтверждения доставки.
        public Task SendDataAsync(byte[] data, CancellationToken cancellationToken = default)
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

            // Копируем: вызывающий код может переиспользовать массив, пока пакет ещё в очереди
            if (!_sendQueue.Writer.TryWrite((byte[])data.Clone()))
            {
                throw new InvalidOperationException($"Cannot send data while in state: {_state}");
            }

            return Task.CompletedTask;
        }

        private async Task ProcessSendQueueAsync()
        {
            try
            {
                await foreach (var dataToSend in _sendQueue.Reader.ReadAllAsync(_closeCts.Token))
                {
                    await SendPacketWithRetryAsync(dataToSend);
                }
            }
            catch (OperationCanceledException)
            {
                // Close() — выходим
            }
        }

        private async Task SendPacketWithRetryAsync(byte[] data)
        {
            var packet = new Packet(data, Interlocked.Increment(ref _nextSendSeq) - 1); // Увеличиваем seq затем используем
            var outgoingPacket = new OutgoingPacket(packet, DateTime.UtcNow);
            _sentPackets[packet.Sequence] = outgoingPacket;

            byte[] bytes = packet.ToBytes();

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (_state != ConnectionState.Established) return; // Соединение разорвано
                if (outgoingPacket.Acked.Task.IsCompleted) return;

                await SendRawAsync(bytes, packet.Type);
                Trace.WriteLine($"[Core.Network.ReliableChannel] Sent packet seq={packet.Sequence}, Type={packet.Type}, Attempt={attempt + 1}");

                // Ждем подтверждения либо таймаута
                try
                {
                    await outgoingPacket.Acked.Task.WaitAsync(TimeSpan.FromMilliseconds(RetryDelayMs), _closeCts.Token);
                    return; // Подтверждён (или отменён сбросом сессии)
                }
                catch (TimeoutException)
                {
                    // Повторяем отправку
                }
                catch (OperationCanceledException)
                {
                    return; // Close()
                }
            }

            // Если после всех попыток пакет не подтвержден — закрываем соединение: получатель выдаёт данные
            // строго по порядку и без этого пакета никогда не отдаст следующие, канал "завис" бы навсегда
            if (_sentPackets.TryRemove(packet.Sequence, out _))
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] Failed to confirm packetSeq={packet.Sequence} after {MaxRetries} retries. Closing connection.");
                Close();
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
                var packet = Packet.FromBytes(e.Data);
                Trace.WriteLine($"[Core.Network.ReliableChannel] Received packet " +
                    $"Seq={packet.Sequence}," +
                    $"Type={packet.Type}," +
                    $"Ack={packet.Acknowledgment}," +
                    $"PayloadLen={packet.Payload.Length}");

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
            if (_sentPackets.TryRemove(ackSeq, out var outgoingPacket))
            {
                Trace.WriteLine($"[Core.Network.ReliableChannel] ACK received for Seq={ackSeq}");
                outgoingPacket.Acked.TrySetResult(); // Пакет успешно подтвержден
            }
            else
            {
                // ACK для пакета который уже был подтвержден или никогда не отправлялся
                Trace.WriteLine($"[Core.Network.ReliableChannel] Duplicate or unexpected ACK for Seq={ackSeq}");
            }
        }

        private void HandleData(Packet packet)
        {
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
                // Получен пакет с более высоким номером
                Trace.WriteLine($"[Core.Network.ReliableChannel] Out of order packet received Seq={packet.Sequence}, Expected={_expectedRecvSeq}. Buffering.");
                _receivedBuffer[packet.Sequence] = packet.Payload;
            }
            // else: packet.Sequence < _expectedRecvSeq -> дубликат, игнорируем (уже обработан)
        }

        private void SendAck(int seqNum)
        {
            var ackPacket = new Packet(Array.Empty<byte>(), 0, seqNum, PacketType.Ack); // Ack не содержит полезной нагрузки
            _ = SendRawAsync(ackPacket.ToBytes(), PacketType.Ack); // Не ждем завершения отправки Ack
            Trace.WriteLine($"[Core.Network.ReliableChannel] Sent ACK for Seq={seqNum}");
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
                    if (_sentPackets.TryRemove(seq, out var stale)) stale.Acked.TrySetResult();
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
        }

        public void Dispose()
        {
            Close();
            _keepAliveTimer.Dispose();
            // _closeCts не освобождаем: фоновые задачи после Close() ещё могут обращаться к его Token
        }

        // -- Вспомогательный класс --
        private class OutgoingPacket
        {
            public Packet Packet { get; }
            public DateTime SentAt { get; set; }
            public TaskCompletionSource Acked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutgoingPacket(Packet packet, DateTime sentAt)
            {
                Packet = packet;
                SentAt = sentAt;
            }
        }
    }
}
//