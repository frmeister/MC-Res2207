// MCTunnel.Tests/NatTraversalIntegrationTests.cs

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using Xunit;

namespace MCTunnel.Tests
{
    public class NatTraversalIntegrationTests
    {
        [Fact]
        public async Task ConnectToHostAsync_WaitForClientAsync_IntegrationTest()
        {
            // Arrange
            int hostPort = 50020;
            var hostEndPoint = new IPEndPoint(IPAddress.Loopback, hostPort);
            IPEndPoint? connectedClientEndpoint = null;
            IPEndPoint? connectedHostEndpoint = null;

            using var hostPeer = new UdpPeer(hostPort);
            using var clientPeer = new UdpPeer(50021); // Используем другой порт для клиента

            // Запускаем получение данных на обоих пирах
            var hostReceiveTask = Task.Run(async () => await hostPeer.StartReceivingAsync());
            var clientReceiveTask = Task.Run(async () => await clientPeer.StartReceivingAsync());

            // Act (Запускаем обе стороны параллельно)
            var waitForClientTask = NatTraversal.WaitForClientAsync(hostPeer);
            var connectToHostTask = NatTraversal.ConnectToHostAsync(clientPeer, hostEndPoint); // Убран CancellationToken

            // Ждём завершения обеих операций (с таймаутом)
            var timeoutTask = Task.Delay(10000); // 10 секунд таймаута
            var finishedTask = await Task.WhenAny(Task.WhenAll(waitForClientTask, connectToHostTask), timeoutTask);

            // Assert
            Assert.NotEqual(timeoutTask, finishedTask); // Убедиться, что не было таймаута

            // Получаем результаты
            connectedClientEndpoint = await waitForClientTask; // Host ждёт клиента
            bool clientConnected = await connectToHostTask;    // Client пытается подключиться

            Assert.True(clientConnected, "Client failed to connect to host.");
            Assert.NotNull(connectedClientEndpoint);
            Assert.Equal(hostEndPoint.Address, connectedClientEndpoint.Address);
            Assert.Equal(50021, connectedClientEndpoint.Port); // Порт клиента
        }

        // Тест для проверки, что ConnectToHostAsync возвращает false при таймауте
        [Fact]
        public async Task ConnectToHostAsync_Timeout_ReturnsFalse()
        {
            // Arrange
            int nonExistentPort = 50022;
            var nonExistentEndpoint = new IPEndPoint(IPAddress.Loopback, nonExistentPort);
            using var clientPeer = new UdpPeer(50023);

            var receiveTask = Task.Run(async () => await clientPeer.StartReceivingAsync());

            // Act
            bool connected = await NatTraversal.ConnectToHostAsync(clientPeer, nonExistentEndpoint); // Убран CancellationToken

            // Assert
            Assert.False(connected, "Client should not have connected to non-existent host.");
        }

        private static UdpPeer CreateLoopbackPeer()
        {
            var peer = new UdpPeer(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(() => peer.StartReceivingAsync());
            return peer;
        }

        [Fact]
        public async Task PunchAsync_BothSidesSimultaneously_ReturnPartnerEndPoints()
        {
            using var peerA = CreateLoopbackPeer();
            using var peerB = CreateLoopbackPeer();

            var results = await Task.WhenAll(
                NatTraversal.PunchAsync(peerA, peerB.LocalEndPoint!, TimeSpan.FromSeconds(5)),
                NatTraversal.PunchAsync(peerB, peerA.LocalEndPoint!, TimeSpan.FromSeconds(5)));

            Assert.Equal(peerB.LocalEndPoint, results[0]);
            Assert.Equal(peerA.LocalEndPoint, results[1]);
            Assert.Equal(peerB.LocalEndPoint, peerA.GetRemoteEndPoint());
        }

        // Люди не нажимают Enter одновременно: вторая сторона начинает позже
        [Fact]
        public async Task PunchAsync_PartnerStartsLater_BothSucceed()
        {
            using var peerA = CreateLoopbackPeer();
            using var peerB = CreateLoopbackPeer();

            var punchA = NatTraversal.PunchAsync(peerA, peerB.LocalEndPoint!, TimeSpan.FromSeconds(10));
            await Task.Delay(1500);
            var punchB = NatTraversal.PunchAsync(peerB, peerA.LocalEndPoint!, TimeSpan.FromSeconds(10));

            Assert.Equal(peerB.LocalEndPoint, await punchA);
            Assert.Equal(peerA.LocalEndPoint, await punchB);
        }

        [Fact]
        public async Task PunchAsync_PartnerSilent_ReturnsNullAfterTimeout()
        {
            using var peerA = CreateLoopbackPeer();
            using var silentPeer = CreateLoopbackPeer();

            var result = await NatTraversal.PunchAsync(peerA, silentPeer.LocalEndPoint!, TimeSpan.FromMilliseconds(500));

            Assert.Null(result);
        }

        [Fact]
        public async Task WaitForClientAsync_Cancelled_ThrowsOperationCanceledException()
        {
            // Arrange
            using var hostPeer = new UdpPeer(50024);
            var receiveTask = Task.Run(async () => await hostPeer.StartReceivingAsync());

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(500);

            // Act & Assert (TaskCanceledException — наследник OperationCanceledException)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await NatTraversal.WaitForClientAsync(hostPeer, cts.Token));
        }

        // Если первый ACK потерялся, клиент шлёт HELLO повторно — хост должен ответить и на него
        [Fact]
        public async Task WaitForClientAsync_RepeatedHello_IsAcknowledgedAgain()
        {
            using var hostPeer = new UdpPeer(50025);
            using var clientPeer = new UdpPeer(50026);
            _ = Task.Run(() => hostPeer.StartReceivingAsync());
            _ = Task.Run(() => clientPeer.StartReceivingAsync());

            int ackCount = 0;
            clientPeer.DataReceived += (_, e) =>
            {
                if (System.Text.Encoding.ASCII.GetString(e.Data) == "ACK") Interlocked.Increment(ref ackCount);
            };

            var hostEndPoint = new IPEndPoint(IPAddress.Loopback, 50025);
            var hello = System.Text.Encoding.ASCII.GetBytes("HELLO");
            var waitForClientTask = NatTraversal.WaitForClientAsync(hostPeer);

            await clientPeer.SendToAsync(hello, hostEndPoint);
            await waitForClientTask.WaitAsync(TimeSpan.FromSeconds(5));
            await clientPeer.SendToAsync(hello, hostEndPoint); // будто первый ACK не дошёл

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref ackCount) < 2 && stopwatch.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(20);
            }

            Assert.Equal(2, Volatile.Read(ref ackCount));
        }
    }
}