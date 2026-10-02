// MCTunnel.Tests/TunnelSessionTests.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;
using MCTunnel.Core.Network;
using Xunit;

namespace MCTunnel.Tests
{
    public class TunnelSessionTests
    {
        // Два соединённых канала: Host открывает порты своих серверов, Client пользуется ими
        private sealed class ConnectedPair : IDisposable
        {
            public UdpPeer PeerA { get; } = CreatePeer();
            public UdpPeer PeerB { get; } = CreatePeer();
            public ReliableChannel ChannelA { get; }
            public ReliableChannel ChannelB { get; }
            public TunnelSession Host { get; }
            public TunnelSession Client { get; }

            private ConnectedPair()
            {
                ChannelA = new ReliableChannel(PeerA);
                ChannelB = new ReliableChannel(PeerB);
                Host = new TunnelSession(ChannelA);
                Client = new TunnelSession(ChannelB);
            }

            public static async Task<ConnectedPair> CreateAsync()
            {
                var pair = new ConnectedPair();
                var connected = await Task.WhenAll(
                    pair.ChannelA.ConnectAsync(pair.PeerB.LocalEndPoint!),
                    pair.ChannelB.ConnectAsync(pair.PeerA.LocalEndPoint!));
                Assert.All(connected, Assert.True);
                return pair;
            }

            // Хост открывает порт, ждём, пока он откроется у клиента
            public async Task<OpenedPort> ShareAsync(ForwardRule rule)
            {
                var opened = new TaskCompletionSource<OpenedPort>(TaskCreationOptions.RunContinuationsAsynchronously);
                Client.RemotePortOpened += (_, port) =>
                {
                    if (port.RemoteRule == rule) opened.TrySetResult(port);
                };
                await Host.ShareAsync(new[] { rule });
                return await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            public void Dispose()
            {
                Host.Dispose();
                Client.Dispose();
                ChannelA.Dispose();
                ChannelB.Dispose();
                PeerA.Dispose();
                PeerB.Dispose();
            }

            private static UdpPeer CreatePeer()
            {
                var peer = new UdpPeer(new IPEndPoint(IPAddress.Loopback, 0));
                _ = Task.Run(() => peer.StartReceivingAsync());
                return peer;
            }
        }

        private static Socket StartUdpEchoServer()
        {
            var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    try
                    {
                        var result = await server.ReceiveFromAsync(buffer, SocketFlags.None, any);
                        await server.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, result.RemoteEndPoint);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { if (server.SafeHandle.IsClosed) break; }
                }
            });
            return server;
        }

        private static TcpListener StartTcpEchoServer()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { break; }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            try { await stream.CopyToAsync(stream); }
                            catch (IOException) { }
                        }
                    });
                }
            });
            return listener;
        }

        private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

        private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

        [Theory]
        [InlineData("udp:28015", ForwardProtocol.Udp, 28015)]
        [InlineData(" TCP : 25565 ", ForwardProtocol.Tcp, 25565)]
        public void ForwardRule_TryParse_ValidInput(string text, ForwardProtocol protocol, int port)
        {
            Assert.True(ForwardRule.TryParse(text, out var rule));
            Assert.Equal(new ForwardRule(protocol, port), rule);
        }

        [Theory]
        [InlineData("")]
        [InlineData("28015")]
        [InlineData("sctp:28015")]
        [InlineData("udp:0")]
        [InlineData("udp:70000")]
        [InlineData("udp:abc")]
        public void ForwardRule_TryParse_InvalidInput(string text)
        {
            Assert.False(ForwardRule.TryParse(text, out _));
        }

        [Fact]
        public async Task Text_IsDeliveredBothWays()
        {
            using var pair = await ConnectedPair.CreateAsync();
            var toClient = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var toHost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Client.TextReceived += (_, text) => toClient.TrySetResult(text);
            pair.Host.TextReceived += (_, text) => toHost.TrySetResult(text);

            await pair.Host.SendTextAsync("привет");
            await pair.Client.SendTextAsync("hello");

            Assert.Equal("привет", await toClient.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("hello", await toHost.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        // Сценарий Rust: игра шлёт UDP на локальный порт, сервер партнёра отвечает
        [Fact]
        public async Task UdpForwarding_DatagramsReachServerAndRepliesComeBack()
        {
            using var server = StartUdpEchoServer();
            using var pair = await ConnectedPair.CreateAsync();
            var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Udp, Port(server)));

            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var sent = Enumerable.Range(0, 10).Select(i => $"datagram {i}").ToList();
            foreach (var message in sent)
            {
                await game.SendAsync(Encoding.UTF8.GetBytes(message), opened.LocalEndPoint);
            }

            var replies = new List<string>();
            while (replies.Count < sent.Count)
            {
                var reply = await game.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(opened.LocalEndPoint, reply.RemoteEndPoint);
                replies.Add(Encoding.UTF8.GetString(reply.Buffer));
            }

            Assert.Equal(sent.OrderBy(s => s), replies.OrderBy(s => s));
        }

        // Сценарий Minecraft: большой объём по TCP должен прийти целиком и без искажений
        [Fact]
        public async Task TcpForwarding_LargeTransfer_EchoedIntact()
        {
            var server = StartTcpEchoServer();
            try
            {
                using var pair = await ConnectedPair.CreateAsync();
                var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Tcp, Port(server)));

                using var game = new TcpClient();
                await game.ConnectAsync(opened.LocalEndPoint);
                var stream = game.GetStream();

                var payload = new byte[1024 * 1024];
                new Random(7).NextBytes(payload);
                var received = new byte[payload.Length];

                var writeTask = stream.WriteAsync(payload).AsTask();
                int total = 0;
                while (total < received.Length)
                {
                    int read = await stream.ReadAsync(received.AsMemory(total)).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.NotEqual(0, read);
                    total += read;
                }
                await writeTask;

                Assert.Equal(payload, received);
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public async Task TcpForwarding_SeveralConnectionsAtOnce_AreIndependent()
        {
            var server = StartTcpEchoServer();
            try
            {
                using var pair = await ConnectedPair.CreateAsync();
                var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Tcp, Port(server)));

                var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
                {
                    using var game = new TcpClient();
                    await game.ConnectAsync(opened.LocalEndPoint);
                    var stream = game.GetStream();
                    var message = Encoding.UTF8.GetBytes(new string((char)('a' + i), 50_000));
                    await stream.WriteAsync(message);

                    var buffer = new byte[message.Length];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                        if (read == 0) break;
                        total += read;
                    }
                    return message.SequenceEqual(buffer);
                }));

                Assert.All(results, Assert.True);
            }
            finally
            {
                server.Stop();
            }
        }

        // Игра закрыла соединение — сервер должен увидеть конец потока и получить всё, что было отправлено до этого
        [Fact]
        public async Task TcpForwarding_GameClosesConnection_ServerReceivesDataAndEof()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var serverReceived = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                    return await reader.ReadToEndAsync(); // Завершится только после закрытия соединения игрой
                });

                using var pair = await ConnectedPair.CreateAsync();
                var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Tcp, Port(listener)));

                using (var game = new TcpClient())
                {
                    await game.ConnectAsync(opened.LocalEndPoint);
                    await game.GetStream().WriteAsync(Encoding.UTF8.GetBytes("bye"));
                }

                Assert.Equal("bye", await serverReceived.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                listener.Stop();
            }
        }

        // Сервер у хоста не запущен — игра партнёра должна сразу получить закрытое соединение, а не висеть
        [Fact]
        public async Task TcpForwarding_ServerNotRunning_LocalConnectionIsClosed()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int closedPort = Port(probe);
            probe.Stop();

            using var pair = await ConnectedPair.CreateAsync();
            var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Tcp, closedPort));

            // На одном компьютере тот же номер порта замкнул бы туннель в петлю: хост подключался бы сам к себе
            Assert.NotEqual(closedPort, opened.LocalEndPoint.Port);

            using var game = new TcpClient();
            await game.ConnectAsync(opened.LocalEndPoint);
            var buffer = new byte[16];

            int read;
            try
            {
                read = await game.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (IOException)
            {
                read = 0; // Соединение сброшено — тоже закрыто
            }

            Assert.Equal(0, read);
        }

        [Fact]
        public async Task ChannelClosed_ClosesLocalPorts()
        {
            var server = StartTcpEchoServer();
            try
            {
                using var pair = await ConnectedPair.CreateAsync();
                var opened = await pair.ShareAsync(new ForwardRule(ForwardProtocol.Tcp, Port(server)));

                pair.ChannelB.Close();
                await Task.Delay(500);

                using var game = new TcpClient();
                await Assert.ThrowsAnyAsync<SocketException>(() => game.ConnectAsync(opened.LocalEndPoint));
            }
            finally
            {
                server.Stop();
            }
        }
    }
}
