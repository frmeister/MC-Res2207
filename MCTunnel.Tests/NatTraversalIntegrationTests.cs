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

        // Этот тест НЕЛЬЗЯ выполнить с текущей сигнатурой NatTraversal.WaitForClientAsync,
        // потому что он не принимает CancellationToken.
        // [Fact]
        // public async Task WaitForClientAsync_Cancelled_ThrowsOperationCanceledException()
        // {
        //     // Arrange
        //     using var hostPeer = new UdpPeer(50024);
        //     var receiveTask = Task.Run(async () => await hostPeer.StartReceivingAsync());

        //     using var cts = new CancellationTokenSource();
        //     cts.CancelAfter(2000); // Отмена через 2 секунды

        //     // Act & Assert
        //     // Требуется изменить NatTraversal.WaitForClientAsync, чтобы он принимал CancellationToken
        //     await Assert.ThrowsAsync<OperationCanceledException>(async () => await NatTraversal.WaitForClientAsync(hostPeer, cts.Token));
        // }
    }
}