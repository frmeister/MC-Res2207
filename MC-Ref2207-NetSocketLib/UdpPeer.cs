// MCTunnel.Core.Network/UdpPeer.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MCTunnel.Core.Network
{
    public class UdpPeer : IDisposable
    {
        private readonly UdpClient _udpClient;
        private IPEndPoint? _remoteEndPoint; // Используем nullable reference type
        private volatile bool _disposed;

        public class UdpDataReceivedEventArgs : EventArgs
        {
            public byte[] Data { get; }
            public IPEndPoint RemoteEndPoint { get; }

            public UdpDataReceivedEventArgs(byte[] data, IPEndPoint remoteEndPoint)
            {
                Data = data ?? throw new ArgumentNullException(nameof(data)); // Проверка аргументов
                RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
            }
        }

        public event EventHandler<UdpDataReceivedEventArgs>? DataReceived; // Nullable reference type для события

        // Окно отправки ReliableChannel и всплески игрового трафика легко переполняют стандартный буфер сокета,
        // и лишние пакеты теряются ещё до нашего кода
        private const int SocketBufferSize = 1 << 20;

        public UdpPeer(int localPort)
        {
            _udpClient = new UdpClient(localPort);
            ConfigureSocket(_udpClient.Client);
        }

        public UdpPeer(IPEndPoint localEndpoint)
        {
            _udpClient = new UdpClient(localEndpoint);
            ConfigureSocket(_udpClient.Client);
        }

        private static void ConfigureSocket(Socket socket)
        {
            socket.ReceiveBufferSize = SocketBufferSize;
            socket.SendBufferSize = SocketBufferSize;
            DisableConnectionResetErrors(socket);
        }

        // Windows: после ICMP "port unreachable" на отправленный пакет следующий ReceiveAsync падает с ConnectionReset (10054).
        // При пробое NAT такие ICMP приходят постоянно (партнёр ещё не запустился), поэтому отключаем это поведение
        internal static void DisableConnectionResetErrors(Socket socket)
        {
            if (!OperatingSystem.IsWindows()) return;

            const int SIO_UDP_CONNRESET = -1744830452; // _WSAIOW(IOC_VENDOR, 12)
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }

        // Send data to specific address
        public async Task SendToAsync(byte[] data, IPEndPoint remoteEndPoint)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpPeer));

            if (data == null || remoteEndPoint == null)
                throw new ArgumentNullException(data == null ? nameof(data) : nameof(remoteEndPoint));

            await _udpClient.SendAsync(data, data.Length, remoteEndPoint);
        }

        // Send data to the pre-set remote endpoint
        public async Task SendAsync(byte[] data)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpPeer));

            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (_remoteEndPoint == null)
                throw new InvalidOperationException("Remote endPoint not set.");
            

            await _udpClient.SendAsync(data, data.Length, _remoteEndPoint);
        }

        public async Task StartReceivingAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpPeer));

            while (!_disposed)
            {
                try
                {
                    // Важно: ReceiveAsync может выбросить исключение при закрытии сокета из другого потока
                    var result = await _udpClient.ReceiveAsync();
                    if (!_disposed) // Проверяем снова после асинхронной операции
                    {
                        OnDataReceived(result.Buffer, result.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Сокет был закрыт, выходим из цикла
                    break;
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.Interrupted) // WSAEINTR
                {
                    // Сокет был прерван, возможно, во время закрытия
                    if (!_disposed)
                    {
                        Trace.WriteLine($"[Core.Network.Receive] Socket interrupted unexpectedly: {se}");
                    }
                    else
                    {
                        // Это ожидаемо при закрытии
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (!_disposed) // Не выводим ошибку, если мы сами закрываемся
                    {
                        Trace.WriteLine($"[Core.Network.Receive] Exception is thrown {ex}");
                    }
                    else
                    {
                        // Если disposed, возможно, исключение связано с закрытием
                        break;
                    }
                }
            }
        }

        protected virtual void OnDataReceived(byte[] data, IPEndPoint remoteEndPoint)
        {
            // Копируем данные, чтобы избежать проблем с изменением буфера в вызывающем коде
            var dataCopy = new byte[data.Length];
            Array.Copy(data, dataCopy, data.Length);

            var handler = DataReceived;
            if (handler != null)
            {
                try
                {
                    handler(this, new UdpDataReceivedEventArgs(dataCopy, remoteEndPoint));
                }
                catch (Exception ex)
                {
                    // Обработка исключения в обработчике события (опционально)
                    Trace.WriteLine($"[Core.Network.OnDataReceived] Handler threw exception: {ex}");
                }
            }
        }

        public void SetRemoteEndPoint(IPEndPoint remoteEndPoint)
        {
            if (remoteEndPoint == null)
                throw new ArgumentNullException(nameof(remoteEndPoint));
            _remoteEndPoint = remoteEndPoint;
        }

        public IPEndPoint? GetRemoteEndPoint()
        {
            return _remoteEndPoint;
        }

        public int LocalPort => _udpClient?.Client != null ? ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port : -1;

        public IPEndPoint? LocalEndPoint => _udpClient?.Client != null ? (IPEndPoint)_udpClient.Client.LocalEndPoint! : null;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    _udpClient?.Close(); // Close() вызывает Dispose()
                }
                catch (ObjectDisposedException)
                {
                    // Игнорируем, если уже закрыт
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Core.Network.Dispose] Error disposing UdpClient: {ex}");
                }
            }
        }
    }
}