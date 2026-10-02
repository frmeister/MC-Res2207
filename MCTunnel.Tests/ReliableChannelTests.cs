// MCTunnel.Tests/ReliableChannelTests.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;
using MCTunnel.Core.Network;
using Xunit;

namespace MCTunnel.Tests
{
    public class ReliableChannelTests
    {
        // Порт 0 — ОС сама выдаёт свободный порт, тесты не конфликтуют друг с другом
        private static UdpPeer CreatePeer()
        {
            var peer = new UdpPeer(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(() => peer.StartReceivingAsync());
            return peer;
        }

        private static async Task ConnectPairAsync(ReliableChannel channelA, UdpPeer peerA, ReliableChannel channelB, UdpPeer peerB)
        {
            var results = await Task.WhenAll(
                channelA.ConnectAsync(peerB.LocalEndPoint!),
                channelB.ConnectAsync(peerA.LocalEndPoint!));

            Assert.True(results[0], "Side A failed to establish the channel.");
            Assert.True(results[1], "Side B failed to establish the channel.");
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds > timeoutMs) return;
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task ConnectAsync_BothSides_EstablishConnection()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            await ConnectPairAsync(channelA, peerA, channelB, peerB);

            Assert.Equal(ConnectionState.Established, channelA.State);
            Assert.Equal(ConnectionState.Established, channelB.State);
        }

        [Fact]
        public async Task SendDataAsync_DeliversAllMessagesInOrder()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            var received = new ConcurrentQueue<string>();
            channelB.DataReceived += (_, data) => received.Enqueue(Encoding.UTF8.GetString(data));

            await ConnectPairAsync(channelA, peerA, channelB, peerB);

            var sent = Enumerable.Range(0, 20).Select(i => $"message {i}").ToList();
            foreach (var message in sent)
            {
                await channelA.SendDataAsync(Encoding.UTF8.GetBytes(message));
            }

            await WaitUntilAsync(() => received.Count >= sent.Count);
            Assert.Equal(sent, received.ToList());
        }

        [Fact]
        public async Task SendDataAsync_WorksInBothDirections()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            var receivedByA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receivedByB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channelA.DataReceived += (_, data) => receivedByA.TrySetResult(Encoding.UTF8.GetString(data));
            channelB.DataReceived += (_, data) => receivedByB.TrySetResult(Encoding.UTF8.GetString(data));

            await ConnectPairAsync(channelA, peerA, channelB, peerB);
            await channelA.SendDataAsync(Encoding.UTF8.GetBytes("from A"));
            await channelB.SendDataAsync(Encoding.UTF8.GetBytes("from B"));

            Assert.Equal("from A", await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("from B", await receivedByA.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task SendDataAsync_LargePayload_IsDelivered()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            channelB.DataReceived += (_, data) => received.TrySetResult(data);

            await ConnectPairAsync(channelA, peerA, channelB, peerB);

            var payload = new byte[40_000];
            new Random(1).NextBytes(payload);
            await channelA.SendDataAsync(payload);

            Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ReceiveDataAsync_WithoutEventSubscribers_ReturnsData()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            await ConnectPairAsync(channelA, peerA, channelB, peerB);
            await channelA.SendDataAsync(Encoding.UTF8.GetBytes("pull me"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var data = await channelB.ReceiveDataAsync(cts.Token);

            Assert.NotNull(data);
            Assert.Equal("pull me", Encoding.UTF8.GetString(data!));
        }

        // После рукопожатия стороны не должны бесконечно перекидываться Hello-пакетами
        [Fact]
        public async Task EstablishedChannels_DoNotFloodHandshakePackets()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();
            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);

            await ConnectPairAsync(channelA, peerA, channelB, peerB);
            await Task.Delay(200); // даём долететь последним ответам рукопожатия

            int handshakePackets = 0;
            peerB.DataReceived += (_, e) =>
            {
                // Тип пакета — первый байт: 2 = Hello, 3 = HelloAck
                if (e.Data.Length > 0 && (e.Data[0] == 2 || e.Data[0] == 3))
                    Interlocked.Increment(ref handshakePackets);
            };
            await Task.Delay(1000);

            Assert.True(handshakePackets <= 2, $"Received {handshakePackets} handshake packets after the connection was established.");
        }

        // Сценарий Program.cs: сначала NatTraversal, затем ReliableChannel на тех же сокетах
        [Fact]
        public async Task NatTraversalThenReliableChannel_ExchangeData()
        {
            using var hostPeer = CreatePeer();
            using var clientPeer = CreatePeer();

            var waitForClientTask = NatTraversal.WaitForClientAsync(hostPeer);
            Assert.True(await NatTraversal.ConnectToHostAsync(clientPeer, hostPeer.LocalEndPoint!));
            var clientEndPoint = await waitForClientTask;
            Assert.NotNull(clientEndPoint);

            using var hostChannel = new ReliableChannel(hostPeer);
            using var clientChannel = new ReliableChannel(clientPeer);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostChannel.DataReceived += (_, data) => received.TrySetResult(Encoding.UTF8.GetString(data));

            var results = await Task.WhenAll(
                hostChannel.ConnectAsync(clientEndPoint!),
                clientChannel.ConnectAsync(hostPeer.LocalEndPoint!));
            Assert.All(results, Assert.True);

            await clientChannel.SendDataAsync(Encoding.UTF8.GetBytes("привет"));
            Assert.Equal("привет", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        // Сценарий Program.cs: одновременный пробой NAT, затем ReliableChannel на тех же сокетах
        [Fact]
        public async Task PunchThenReliableChannel_ExchangeData()
        {
            using var peerA = CreatePeer();
            using var peerB = CreatePeer();

            var punched = await Task.WhenAll(
                NatTraversal.PunchAsync(peerA, peerB.LocalEndPoint!, TimeSpan.FromSeconds(5)),
                NatTraversal.PunchAsync(peerB, peerA.LocalEndPoint!, TimeSpan.FromSeconds(5)));

            using var channelA = new ReliableChannel(peerA);
            using var channelB = new ReliableChannel(peerB);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            channelB.DataReceived += (_, data) => received.TrySetResult(Encoding.UTF8.GetString(data));

            var connected = await Task.WhenAll(
                channelA.ConnectAsync(punched[0]!),
                channelB.ConnectAsync(punched[1]!));
            Assert.All(connected, Assert.True);

            await channelA.SendDataAsync(Encoding.UTF8.GetBytes("через пробитый NAT"));
            Assert.Equal("через пробитый NAT", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ConnectAsync_WithoutRemoteChannel_ReturnsFalseAfterTimeout()
        {
            using var peerA = CreatePeer();
            using var silentPeer = CreatePeer(); // UDP-сокет есть, но ReliableChannel на нём никто не поднял
            using var channelA = new ReliableChannel(peerA);

            bool connected = await channelA.ConnectAsync(silentPeer.LocalEndPoint!, handshakeTimeoutMs: 300);

            Assert.False(connected);
            Assert.Equal(ConnectionState.Disconnected, channelA.State);
        }

        [Fact]
        public async Task Close_DuringConnect_AbortsHandshakeAndKeepsClosedState()
        {
            using var peerA = CreatePeer();
            using var silentPeer = CreatePeer();
            using var channelA = new ReliableChannel(peerA);

            var stopwatch = Stopwatch.StartNew();
            var connectTask = channelA.ConnectAsync(silentPeer.LocalEndPoint!);
            await Task.Delay(100);
            channelA.Close();

            bool connected = await connectTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(connected);
            Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"ConnectAsync kept waiting {stopwatch.ElapsedMilliseconds} ms after Close().");
            Assert.Equal(ConnectionState.Closed, channelA.State);
        }
    }
}
