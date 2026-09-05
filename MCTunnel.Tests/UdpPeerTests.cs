// MCTunnel.Tests/UdpPeerTests.cs

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using Xunit; // Подключаем xUnit

namespace MCTunnel.Tests
{
    public class UdpPeerTests
    {
        // Тест для проверки отправки и получения данных между двумя UdpPeer
        [Fact]
        public async Task SendToAsync_WhenDataSent_ReceivesData()
        {
            // Arrange (Подготовка)
            var receivedData = new byte[0]; // Переменная для хранения полученных данных
            var receivedFrom = (IPEndPoint)null; // Переменная для хранения адреса отправителя
            var signal = new TaskCompletionSource<bool>(); // Для синхронизации получения

            int port1 = 50001;
            int port2 = 50002;

            using var peer1 = new UdpPeer(port1); // Peer, который отправляет
            using var peer2 = new UdpPeer(port2); // Peer, который принимает

            // Подписываемся на событие получения данных у peer2
            peer2.DataReceived += (sender, args) =>
            {
                receivedData = args.Data;
                receivedFrom = args.RemoteEndPoint;
                signal.SetResult(true); // Сообщаем, что данные получены
            };

            // Запускаем получение на peer2 в фоне
            var receiveTask = Task.Run(async () => await peer2.StartReceivingAsync());

            byte[] testData = System.Text.Encoding.UTF8.GetBytes("Hello from Peer1!");
            var targetEndpoint = new IPEndPoint(IPAddress.Loopback, port2); // Peer2's address

            // Act (Действие)
            await peer1.SendToAsync(testData, targetEndpoint);

            // Assert (Проверка)
            // Ждём, пока данные не будут получены (с таймаутом)
            var completedTask = await Task.WhenAny(signal.Task, Task.Delay(2000)); // Таймаут 2 секунды
            Assert.True(completedTask == signal.Task, "Data was not received within the timeout.");

            Assert.Equal(testData, receivedData); // Проверяем, что полученные данные совпадают
            Assert.NotNull(receivedFrom); // Проверяем, что адрес отправителя не null
            Assert.Equal(port1, receivedFrom.Port); // Проверяем, что данные пришли с правильного порта peer1
        }

        // Тест для проверки метода SetRemoteEndPoint и SendAsync (без указания адреса)
        [Fact]
        public async Task SendAsync_WithRemoteEndPointSet_SendsData()
        {
            // Arrange
            var receivedData = new byte[0];
            var receivedFrom = (IPEndPoint)null;
            var signal = new TaskCompletionSource<bool>();

            int port1 = 50003;
            int port2 = 50004;

            using var peer1 = new UdpPeer(port1); // Peer, который отправляет
            using var peer2 = new UdpPeer(port2); // Peer, который принимает

            peer2.DataReceived += (sender, args) =>
            {
                receivedData = args.Data;
                receivedFrom = args.RemoteEndPoint;
                signal.SetResult(true);
            };

            var receiveTask = Task.Run(async () => await peer2.StartReceivingAsync());

            byte[] testData = System.Text.Encoding.UTF8.GetBytes("Hello via SetRemoteEndPoint!");
            var targetEndpoint = new IPEndPoint(IPAddress.Loopback, port2);

            // Устанавливаем удалённый адрес для peer1
            peer1.SetRemoteEndPoint(targetEndpoint);

            // Act
            await peer1.SendAsync(testData); // Отправляем без указания адреса

            // Assert
            var completedTask = await Task.WhenAny(signal.Task, Task.Delay(2000));
            Assert.True(completedTask == signal.Task, "Data was not received within the timeout.");

            Assert.Equal(testData, receivedData);
            Assert.NotNull(receivedFrom);
            Assert.Equal(port1, receivedFrom.Port);
        }

        // Тест для проверки LocalPort
        [Fact]
        public void LocalPort_ReturnsCorrectPort()
        {
            // Arrange
            int expectedPort = 50005;
            using var peer = new UdpPeer(expectedPort);

            // Act & Assert
            Assert.Equal(expectedPort, peer.LocalPort);
        }

        // Тест для проверки LocalEndPoint
        [Fact]
        public void LocalEndPoint_ReturnsCorrectEndPoint()
        {
            // Arrange
            int expectedPort = 50006;
            using var peer = new UdpPeer(expectedPort);

            // Act
            var localEndPoint = peer.LocalEndPoint;

            // Assert
            Assert.NotNull(localEndPoint);
            Assert.Equal(expectedPort, localEndPoint.Port);
            // Можно также проверить IP, но он может быть 0.0.0.0 или 127.0.0.1 в зависимости от конфигурации
        }

        // Тест для проверки Dispose
        [Fact]
        public async Task Dispose_ClosesUdpClient()
        {
            // Arrange
            using var peer = new UdpPeer(50007); // Используем using, он вызовет Dispose
            var client = peer.GetType().GetField("_udpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(peer) as UdpClient;
            Assert.NotNull(client);
            Assert.False(client.Client.Connected); // UdpClient.Client.Connected не всегда показывает открытость, но можно проверить состояние
            Assert.Equal(System.Net.Sockets.SocketType.Dgram, client.Client.SocketType);

            // Act
            peer.Dispose(); // Явный вызов Dispose, хотя using уже вызовет его

            // Assert
            // Проверить, что сокет закрыт, можно попытавшись выполнить операцию
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await peer.SendToAsync(new byte[1], new IPEndPoint(IPAddress.Loopback, 50008)));
        }

        // Тест для проверки, что SendAsync выбрасывает исключение, если RemoteEndPoint не установлен
        [Fact]
        public async Task SendAsync_WithoutRemoteEndPoint_ThrowsInvalidOperationException()
        {
            // Arrange
            using var peer = new UdpPeer(50009);
            byte[] testData = new byte[1];

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => peer.SendAsync(testData));
        }

        // Тест для проверки, что SendToAsync и SendAsync выбрасывают исключение, если передан null
        [Fact]
        public async Task SendMethods_WithNullData_ThrowArgumentNullException()
        {
            // Arrange
            using var peer = new UdpPeer(50010);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 50011);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() => peer.SendToAsync(null, endpoint));
            await Assert.ThrowsAsync<ArgumentNullException>(() => peer.SendAsync(null));
        }

        // Тест для проверки, что SetRemoteEndPoint выбрасывает исключение, если передан null
        [Fact]
        public void SetRemoteEndPoint_WithNullEndPoint_ThrowsArgumentNullException()
        {
            // Arrange
            using var peer = new UdpPeer(50012);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => peer.SetRemoteEndPoint(null));
        }

    }
}