using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
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

        // -- Состояния и зависимости --
        private ConnectionState _state = ConnectionState.Disconnected;
        private readonly object _stateLock = new object(); // Объект для синхронизации
        private readonly UdpPeer _udpPeer;
        private IPEndPoint? _remoteEndPoint;
        private readonly Timer _inactivityTimer;

        // -- Для отправки --
        private int _nextsendSeq = 0; // Следующий номер последовательности для отправки
        private readonly ConcurrentDictionary<int, OutgoingPacket> _sentPackets = new(); // Содержит очередь пакетов для отправления
        private readonly Queue<byte[]> _sendQueue = new(); // Очередь данных для отправки
        private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

        // -- Для получения --
        private int _expectedRecvSeq = 0; // Ожидаемый номер последовательности для получения
        private readonly SortedList<int, byte[]> _receivedBuffer = new(); // Буффер для полученых данных
        private readonly ConcurrentQueue<byte[]> _receiveQueue = new(); // Очередь для полученных данных
        private readonly SemaphoreSlim _receivedSemaphore = new(0, int.MaxValue);

        // -- События --
        public EventHandler<byte[]?> DataReceived; // Событие для полученных данных

        public ReliableChannel(UdpPeer udpPeer)
        {
            _udpPeer = udpPeer ?? throw new ArgumentNullException(nameof(udpPeer));
            // Подписались на событие получения данных
            _udpPeer.DataReceived += OnUpdDataaReceived;

            // Инициализируем таймер неактивности
            _inactivityTimer = new Timer(CheckInactivity, null, Timeout.Infinite, Timeout.Infinite);

            // Запускаем фоновые задачи
            Task.Run(ProcessSendQueueAsync);
            Task.Run(ProcessReceiveQueueAsync);
        }

        public ConnectionState State => _state;

        // -- Управление соединением --
        public void Connect(IPEndPoint remoteEndPoint)
        {
            if (_state != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"Cannot connect while in state: {_state}");
            }

            _remoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
            _state = ConnectionState.Connecting;

            // Запускаем таймер неактивности
            _inactivityTimer.Change(InactivityTimeoutMs, InactivityTimeoutMs);

            // Отправляем Hello пакет (или начальный SYN пакет, если будет усложнённый handshake)
            // Для простоты, считаем, что соединение установлено сразу после получения первого пакета от партнёра
            // или после успешного hole punching (что происходит до вызова Connect).
            // В реальности здесь может быть сложный handshake.
            // Пока просто переходим в Established, когда знаем remoteEndPoint.
            _state = ConnectionState.Established;
            // TODO: Возможно, стоит отправить специальный пакет подтверждения соединения.
        }

        // -- Отправка данных --
        public async Task SendDataAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (_state != ConnectionState.Established)
            {
                throw new InvalidOperationException($"Cannot send data while in state: {_state}");
            }
            if (data == null) throw new ArgumentException(nameof(data));

            lock (_sendQueue)
            {
                _sendQueue.Enqueue(data);
            }
            _sendSemaphore.Release(); // Уведомляем ProcessSendQueueAsync о новом элементе

            // Ждём, пока данные не будут надёжно отправлены (опционально)
            // Это может быть сложно реализовать без блокировки.
            // Обычно просто добавляют в очередь и возвращаются.
        }

        private async Task ProcessSendQueueAsync()
        {
            while (_state != ConnectionState.Closed && _state != ConnectionState.Disconnected)
            {
                await _sendSemaphore.WaitAsync(); // Ждем пока кто-то не добавит данные в очередь

                byte[]? dataToSend = null;
                lock (_sendQueue)
                {
                    if (_sendQueue.Count > 0)
                    {
                        dataToSend = _sendQueue.Dequeue();
                    }
                }

                if (dataToSend != null && _state == ConnectionState.Established && _remoteEndPoint != null)
                {
                    await SendPacketWithRetryAsync(dataToSend);
                }
            }
        }

        private async Task SendPacketWithRetryAsync(byte[] data)
        {
            var packet = new Packet(data, Interlocked.Increment(ref _nextSendSeq) - 1); // Увеличиваем seq затем используем
            var outgoingPacket = new OutgoingPacket(packet, DateTime.UtcNow);

            _sentPackets.TryAdd(packet.Sequence, outgoingPacket);

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (_state != ConnectionState.Established || _remoteEndPoint == null) break; // Соединение разорвано

                try
                {
                    byte[] bytes = packet.ToBytes();
                    await _udpPeer.SendToAsync(bytes, _remoteEndPoint);
                    Debug.WriteLine($"[Core.Network.ReliableChannel] Sent packet seq={packet.Sequence}, Type={packet.Type}, Attempt={attempt + 1}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ReliableChannel] Failed to send packet Seq={packet.Sequence}, Error: {ex.Message}");
                    // TODO: Логировать ошибку отправки
                }

                // Ждем подтверждения либо таймаута
                bool confirmed = false;
                DateTime lastSendTime = DateTime.UtcNow;
                while (!confirmed && DateTime.UtcNow.Subtract(lastSendTime).TotalMilliseconds < RetryDelayMs)
                {
                    if (_sentPackets.ContainsKey(packet.Sequence))
                    {
                        await Task.Delay(10, CancellationToken.None);
                    }
                    else
                    {
                        confirmed = true;
                    }
                }

                if (confirmed) break;
            }

            // Если после всех попыток пакет не подтвержден, удаляем его и возможно закрываем соединение
            if (_sentPackets.ContainsKey(packet.Sequence))
            {
                Debug.WriteLine($"[Core.Network.ReliableChannel] Failed to confirm packetSeq={packet.Sequence} after {MaxRetries} retries. Closing connection.");
                _sentPackets.TryRemove(packet.Sequence, out _);
                // TODO: Закрыть соединение по таймауту
                // Close();
            }
        }

        // -- Получение данных --
        private void OnUdpDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
        {
            if (_state != ConnectionState.Established) return; // Игнорируем если не установлено соединение
            if (_remoteEndPoint != null && !e.RemoteEndPoint.Equals(_remoteEndPoint)) return; // Игнорируем если не от нашего партнера

            // Сброс таймера неактивности
            _inactivityTimer.Change(InactivityTimeoutMs, InactivityTimeoutMs);

            try
            {
                var packet = Packet.FromBytes(e.Data);
                Debug.WriteLine($"[Core.Network.ReliableChannel] Received packet" +
                    $"Seq={packet.Sequence}," +
                    $"Type={packet.Type}," +
                    $"Ack={packet.Acknowledgment}," +
                    $"PayloadLen={packet.Payload.Length}");

                switch(packet.Type)
                {
                    case PacketType.Ack:
                        HandleAck(packet.Acknowledgment);
                        break;
                    case PacketType.Data:
                        HandleData(packet);
                        break;
                    case PacketType.Hello:
                        // Игнорируем или обрабатывем как пакет для подключения или подтверждения
                        // SenAck(packet.Sequence + 1) // Подтверждаем Hello
                        break;
                    default:
                        Debug.WriteLine($"[Core.Network.ReliableChannel] Unknown packet type: {packet.Type}");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"[Core.Network.ReliableChannel] Invalid packet received: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Core.Network.RelaibleChannel] Error processing received pakcet: {ex.Message}");
            }
        }

        private void HandleAck(int ackSeq)
        {
            if (_sentPackets.TryRemove(ackSeq, out _))
            {
                Debug.WriteLine($"[Core.Network.ReliableChannel] ACK received for Seq={ackSeq}");
                // Пакет успешно подтвержден, можно убрать из очереди
            }
            else
            {
                // ACK для пакета который уже юыл подтвержден или никогда не отправлялся
                Debug.WriteLine($"[Core.Network.ReliableChannel] Duplicate or unexpected ACK for Seq={ackSeq}");
            }
        }

        private void HandleData(Packet packet)
        {
            // Отпаравляем ACK для полученного пакета
            SendAck(packet.Sequence);

            if (packet.Sequence == _expectedRecvSeq)
            {
                // Получен ожидаемый пакет
                EnqueueReceivedData(packet.Payload);
                _expectedRecvSeq++;

                // Проверяем буффер на наличие следующих по порядку пакетов
                while (_receivedBuffer.ContainsKey(_expectedRecvSeq))
                {
                    if (_receivedBuffer.TryGetValue(_expectedRecvSeq, out var bufferedData))
                    {
                        EnqueueReceivedData(bufferedData);
                        _receivedBuffer.RemoveAt(0);
                        _expectedRecvSeq++;
                    }
                }
            }
            else if (packet.Sequence > _expectedRecvSeq)
            {
                // Получен пакет с более высоким номером
                Debug.WriteLine($"[Core.Network.ReliableChannel] Out of order packet received Seq={packet.Sequence}, Expected={_expectedRecvSeq}. Buffering.");
                _receivedBuffer[packet.Sequence] = packet.Payload;
            }
            // else: packet.Sequence < _expectedRecvSeq -> дубликат, игнорируем (уже обработан)
        }

        private void SendAck(int seqNum)
        {
            if (_remoteEndPoint != null)
            {
                var ackPacket = new Packet(Array.Empty<byte>(), 0, seqNum, PacketType.Ack); // Ack не содержит полезной нагрузки
                var ackBytes = ackPacket.ToBytes();
                _ = _udpPeer.SendToAsync(ackBytes, _remoteEndPoint); // Не ждем завершения отправки Ack
                Debug.WriteLine($"[Core.Network.ReliableChannel] Sent ACK for Seq={seqNum}");
            }
        }

        private void EnqueueReceivedData(byte[] data)
        {
            _receiveQueue.Enqueue(data);
            _receivedSemaphore.Release();
        }

        private async Task ProcessReceiveQueueAsync()
        {
            while (_state != ConnectionState.Closed && _state != ConnectionState.Disconnected)
            {
                await _receivedSemaphore.WaitAsync(); // Ждем пока кто-то не добавитд анные в очередь

                byte[]? dataToProcess = null;
                lock(_receiveQueue)
                {
                    if (_receiveQueue.Count > 0)
                    {
                        if (_receiveQueue.TryDequeue(out dataToProcess)) { } // fix
                    }
                }

                if (dataToProcess != null)
                {
                    // Вызываем событие для вернего уровня (например TcpBridge)
                    DataReceived?.Invoke(this, dataToProcess);
                }
            }
        }

        // -- Чтение данных (опционально, если не использовать события) --
        public async Task<byte[]?> ReceiveDataAsync(CancellationToken cancellationToken = default)
        {
            if (_state != ConnectionState.Established)
            {
                throw new InvalidCastException($"Cannot receive data while in state: {_state}");
            }

            await _receivedSemaphore.WaitAsync(cancellationToken);
            lock(_receiveQueue)
            {
                if (_receiveQueue.Count > 0)
                {
                    byte[]? dequeuedData = null;
                    if (_receiveQueue.TryDequeue(out dequeuedData)) return dequeuedData; // fix1
                    else return null; // fix1
                }
            }
            return null; // Не должно происходить, если семафор сработал
        }
        // -- Проверка неактивности --
        private void CheckInactivity(object? state)
        {
            // Простая проверка: если соединение установлено, но дано ничего не происходило
            // В реальности можно отслеживать время последней отправки/получения
            if (_state == ConnectionState.Established)
            {
                // TODO: Реализовать логику проверки неактивности (например, копрака keep-alive или просто закрытие)
                Debug.WriteLine("[Core.Network.ReliableChannel] Inactivity detected, closing connection.");
                Close();
            }
        }

        // -- Закрытие --
        public void Close()
        {
            // var oldState = Interlocked.Exchange(ref _state, ConnectionState.Closed);
            // if (oldState == ConnectionState.Closed) return;

            lock(_stateLock)
            {
                if (_state == ConnectionState.Closed) return; // Проверяем и меняем состояние атомарно
                _state = ConnectionState.Closed;
            }

            _inactivityTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _inactivityTimer?.Dispose();
            _sendSemaphore?.Release(); // Прервать ожидание в ProcessSendQueueAsync
            _receivedSemaphore?.Release(); // Прервать ожидание в ProcessReceiveQueueAsync
            // Очистить очереди, таймеры и т.д.
        }

        private void OnUpdDataaReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            Close();
            _inactivityTimer?.Dispose();
            _sendSemaphore?.Dispose();
            _receivedSemaphore?.Dispose();
            // Остальные ресурсы;
        }

        // -- Вспомогательный класс --
        private class OutgoingPacket
        {
            public Packet Packet { get; }
            public DateTime SentAt { get; set; }

            public OutgoingPacket(Packet packet, DateTime sentAt)
            {
                Packet = packet;
                SentAt = sentAt;
            }
        }
    }
}
