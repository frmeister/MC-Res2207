// MCTunnel.Tests/RoomTests.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MC_Ref2207_NetSocketLib;
using MCTunnel.Core.Network;
using MCTunnel.Core.Rooms;
using Xunit;

namespace MCTunnel.Tests
{
    public class RoomProtocolTests
    {
        [Fact]
        public void JoinAndWelcome_RoundTrip()
        {
            Assert.True(RoomProtocol.TryParseJoin(RoomProtocol.BuildJoin("Марина"), out var version, out var name));
            Assert.Equal(RoomProtocol.Version, version);
            Assert.Equal("Марина", name);

            var welcome = new RoomWelcome(7, 10, "rust", "Лёша", 1);
            Assert.True(RoomProtocol.TryParseWelcome(RoomProtocol.BuildWelcome(welcome), out _, out var parsed));
            Assert.Equal(welcome, parsed);
        }

        [Fact]
        public void Roster_RoundTripKeepsStatusAndPing()
        {
            var members = new List<RoomMember>
            {
                new(0, "Лёша", RoomMemberStatus.Host, null),
                new(1, "Марина", RoomMemberStatus.InGame, 34),
                new(2, "kostya_2008", RoomMemberStatus.Joining, null),
            };

            Assert.True(RoomProtocol.TryParseRoster(RoomProtocol.BuildRoster(members), out var parsed));
            Assert.Equal(members, parsed);
        }

        [Fact]
        public void Chat_RoundTrip()
        {
            var message = new RoomChatMessage(3, "Тёма", "у меня 96 мс, вроде норм");
            Assert.True(RoomProtocol.TryParseChat(RoomProtocol.BuildChat(message), out var parsed));
            Assert.Equal(message, parsed);
        }

        // Кадр пришёл из сети: обрезанный не должен ронять разбор
        [Fact]
        public void TruncatedFrames_AreRejected()
        {
            var roster = RoomProtocol.BuildRoster(new[] { new RoomMember(1, "Марина", RoomMemberStatus.InRoom, 40) });
            for (int length = 1; length < roster.Length; length++)
            {
                Assert.False(RoomProtocol.TryParseRoster(roster[..length], out _));
            }
            Assert.False(RoomProtocol.TryParseWelcome(new byte[] { RoomProtocol.Welcome, 1 }, out _, out _));
        }

        [Fact]
        public void LongNames_AreShortenedToLimit()
        {
            Assert.True(RoomProtocol.TryParseJoin(RoomProtocol.BuildJoin(new string('ё', 100)), out _, out var name));
            Assert.Equal(RoomProtocol.MaxNameLength, name.Length);
        }

        [Fact]
        public void PingAndPong_CarryTimestamp()
        {
            var ping = RoomProtocol.BuildPing(5, 123456789);
            Assert.True(RoomProtocol.TryParsePong(RoomProtocol.BuildPong(ping), out var number, out var timestamp));
            Assert.Equal(5u, number);
            Assert.Equal(123456789, timestamp);
        }
    }

    public class RoomTests
    {
        private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(200);

        private static Task<RoomHost> OpenHostAsync(int maxPlayers = 10, int port = 0, params ForwardRule[] shares) =>
            RoomHost.OpenAsync(new RoomHostOptions
            {
                HostName = "Лёша",
                GameId = "rust",
                Port = port,
                BindAddress = IPAddress.Loopback,
                MaxPlayers = maxPlayers,
                Shares = shares,
                DiscoverPublicAddress = false,
                PingInterval = Fast,
            });

        private static Task<RoomClient> JoinAsync(RoomHost host, string name, TimeSpan? handshake = null) =>
            RoomClient.JoinAsync(new IPEndPoint(IPAddress.Loopback, host.Port), ClientOptions(name, handshake));

        private static RoomClientOptions ClientOptions(string name, TimeSpan? handshake = null) => new()
        {
            PlayerName = name,
            BindAddress = IPAddress.Loopback,
            PingInterval = Fast,
            LostAfter = TimeSpan.FromSeconds(1),
            HandshakeTimeout = handshake ?? TimeSpan.FromSeconds(10),
            ReconnectTimeout = TimeSpan.FromSeconds(5),
        };

        private static async Task WaitUntilAsync(Func<bool> condition, string what, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Не дождались: {what}");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task TwoPlayers_SeeEachOther_ChatThroughHost_AndMeasurePing()
        {
            using var host = await OpenHostAsync();
            using var marina = await JoinAsync(host, "Марина");
            using var tyoma = await JoinAsync(host, "Тёма");

            Assert.Equal("Лёша", marina.HostName);
            Assert.Equal("rust", marina.GameId);
            Assert.NotEqual(marina.MemberId, tyoma.MemberId);

            await WaitUntilAsync(() => marina.Members.Count == 3 && tyoma.Members.Count == 3, "все видят троих");
            Assert.Equal(new[] { "Лёша", "Марина", "Тёма" }, host.Members.Select(m => m.Name));

            // Чат игрока доходит до хоста и другого игрока, автору эхо не приходит
            var tyomaInbox = new ConcurrentQueue<RoomChatMessage>();
            var marinaInbox = new ConcurrentQueue<RoomChatMessage>();
            var hostInbox = new ConcurrentQueue<RoomChatMessage>();
            tyoma.ChatReceived += (_, m) => tyomaInbox.Enqueue(m);
            marina.ChatReceived += (_, m) => marinaInbox.Enqueue(m);
            host.ChatReceived += (_, m) => hostInbox.Enqueue(m);

            await marina.SendChatAsync("Я на месте");
            await WaitUntilAsync(() => tyomaInbox.Count == 1 && hostInbox.Count == 1, "сообщение Марины");
            Assert.Equal("Марина", tyomaInbox.Single().AuthorName);
            Assert.Equal("Я на месте", hostInbox.Single().Text);

            await host.SendChatAsync("Через пять минут рейд");
            await WaitUntilAsync(() => marinaInbox.Any(m => m.AuthorName == "Лёша"), "сообщение хоста");
            Assert.DoesNotContain(marinaInbox, m => m.AuthorName == "Марина");

            // Пинг меряют обе стороны
            await WaitUntilAsync(() => marina.PingMs != null, "пинг у игрока");
            await WaitUntilAsync(() => host.Members.Skip(1).All(m => m.PingMs != null && m.Status == RoomMemberStatus.InRoom),
                "пинг и статус у хоста");
            await WaitUntilAsync(() => tyoma.Members.Single(m => m.Name == "Марина").PingMs != null, "пинг Марины в списке у Тёмы");
        }

        [Fact]
        public async Task GameTraffic_GoesThroughTunnel_AndMarksPlayerInGame()
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await server.ReceiveAsync();
                        await server.SendAsync(request.Buffer, request.Buffer.Length, request.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });

            using var host = await OpenHostAsync(shares: new ForwardRule(ForwardProtocol.Udp, serverPort));
            using var marina = await JoinAsync(host, "Марина");

            var opened = Assert.Single(marina.OpenedPorts);
            Assert.Equal(serverPort, opened.RemoteRule.Port);

            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var hello = Encoding.UTF8.GetBytes("hello server");
            await game.SendAsync(hello, hello.Length, opened.LocalEndPoint);
            var reply = await game.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(hello, reply.Buffer);

            await WaitUntilAsync(() => host.Members.Single(m => m.Name == "Марина").Status == RoomMemberStatus.InGame, "статус «в игре»");
        }

        [Fact]
        public async Task FullRoom_RejectsExtraPlayer()
        {
            using var host = await OpenHostAsync(maxPlayers: 2);
            using var marina = await JoinAsync(host, "Марина");

            var error = await Assert.ThrowsAsync<RoomConnectException>(() => JoinAsync(host, "Тёма"));
            Assert.Equal(RoomConnectError.RoomFull, error.Error);
        }

        [Fact]
        public async Task Leave_MarksMemberLeft()
        {
            using var host = await OpenHostAsync();
            var marina = await JoinAsync(host, "Марина");

            await marina.LeaveAsync();

            await WaitUntilAsync(() => host.Members.Single(m => m.Name == "Марина").Status == RoomMemberStatus.Left, "Марина вышла");
            Assert.NotNull(host.Members.Single(m => m.Name == "Марина").LeftAt);
        }

        [Fact]
        public async Task CloseRoom_NotifiesPlayers()
        {
            var host = await OpenHostAsync();
            using var marina = await JoinAsync(host, "Марина");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            marina.RoomClosed += (_, _) => closed.TrySetResult();

            await host.CloseAsync();

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(marina.IsConnected);
        }

        [Fact]
        public async Task UnreachableHost_ReportsHostNotResponding()
        {
            // Порт, на котором никого нет
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

            var error = await Assert.ThrowsAsync<RoomConnectException>(() =>
                RoomClient.JoinAsync(new IPEndPoint(IPAddress.Loopback, port), ClientOptions("Марина", TimeSpan.FromSeconds(1))));
            Assert.Equal(RoomConnectError.HostNotResponding, error.Error);
        }

        private static int FreeUdpPort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        // «Впустить по адресу»: хост шлёт KeepAlive на адрес игрока, чтобы свой роутер и брандмауэр пропустили его Hello
        [Fact]
        public async Task LetIn_SendsKeepAlivesToPlayerAddress()
        {
            using var host = await OpenHostAsync();
            using var player = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            host.LetIn((IPEndPoint)player.Client.LocalEndPoint!, TimeSpan.FromSeconds(2));

            var received = await player.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(PacketType.KeepAlive, Packet.FromBytes(received.Buffer).Type);
            Assert.Equal(host.Port, received.RemoteEndPoint.Port);
        }

        [Fact]
        public async Task LetIn_DoesNotDisturbJoiningPlayer()
        {
            using var host = await OpenHostAsync();
            int playerPort = FreeUdpPort();
            host.LetIn(new IPEndPoint(IPAddress.Loopback, playerPort));

            var options = ClientOptions("Марина");
            using var marina = await RoomClient.JoinAsync(new IPEndPoint(IPAddress.Loopback, host.Port),
                new RoomClientOptions { PlayerName = "Марина", BindAddress = IPAddress.Loopback, LocalPort = playerPort, PingInterval = options.PingInterval });

            await WaitUntilAsync(() => host.Members.Any(m => m.Name == "Марина"), "Марина в комнате");
        }

        // Порт для постоянного адреса занят (например, на этом компьютере открыта комната) — подключаемся с любого
        [Fact]
        public async Task BusyLocalPort_FallsBackToRandomPort()
        {
            using var host = await OpenHostAsync();
            using var busy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int busyPort = ((IPEndPoint)busy.Client.LocalEndPoint!).Port;

            using var marina = await RoomClient.JoinAsync(new IPEndPoint(IPAddress.Loopback, host.Port),
                new RoomClientOptions { PlayerName = "Марина", BindAddress = IPAddress.Loopback, LocalPort = busyPort });

            Assert.True(marina.IsConnected);
        }

        // Хост пропал (например, перезапустил приложение) и снова открыл комнату на том же порту:
        // игрок замечает потерю связи, переподключается, и игра продолжает ходить на тот же локальный порт
        [Fact]
        public async Task LostHost_ReconnectsAndKeepsLocalPort()
        {
            var rule = new ForwardRule(ForwardProtocol.Tcp, 25565);
            var host = await OpenHostAsync(shares: rule);
            int port = host.Port;
            using var marina = await JoinAsync(host, "Марина");
            int localPort = Assert.Single(marina.OpenedPorts).LocalEndPoint.Port;

            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            marina.ConnectionLost += (_, _) => lost.TrySetResult();
            host.Dispose(); // Без «комната закрыта» — как будто связь оборвалась
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(marina.IsConnected);

            using var reopened = await OpenHostAsync(port: port, shares: rule);
            Assert.True(await marina.ReconnectAsync());
            Assert.True(marina.IsConnected);
            Assert.Equal(localPort, Assert.Single(marina.OpenedPorts).LocalEndPoint.Port);
            await WaitUntilAsync(() => reopened.Members.Any(m => m.Name == "Марина"), "Марина снова в комнате");
        }
    }
}
