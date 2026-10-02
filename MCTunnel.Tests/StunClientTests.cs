// MCTunnel.Tests/StunClientTests.cs

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using Xunit;

namespace MCTunnel.Tests
{
    public class StunClientTests
    {
        private static UdpPeer CreatePeer()
        {
            var peer = new UdpPeer(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(() => peer.StartReceivingAsync());
            return peer;
        }

        // Простейший STUN-сервер: отвечает на Binding Request адресом отправителя в XOR-MAPPED-ADDRESS.
        // mapAddress позволяет изобразить NAT, который подменяет адрес.
        private static UdpPeer CreateFakeStunServer(Func<IPEndPoint, IPEndPoint>? mapAddress = null)
        {
            var server = CreatePeer();
            server.DataReceived += (_, e) =>
            {
                if (e.Data.Length < 20 || e.Data[0] != 0x00 || e.Data[1] != 0x01) return;

                var mapped = mapAddress?.Invoke(e.RemoteEndPoint) ?? e.RemoteEndPoint;
                byte[] cookie = { 0x21, 0x12, 0xA4, 0x42 };
                byte[] address = mapped.Address.GetAddressBytes();
                int xorPort = mapped.Port ^ 0x2112;

                var response = new List<byte> { 0x01, 0x01, 0x00, 0x00 }; // Binding Success Response, длина — ниже
                response.AddRange(e.Data.AsSpan(4, 16).ToArray());          // magic cookie + transaction id
                // SOFTWARE длиной 5 байт: проверяет выравнивание атрибутов по 4 байта
                response.AddRange(new byte[] { 0x80, 0x22, 0x00, 0x05, (byte)'f', (byte)'a', (byte)'k', (byte)'e', (byte)'!', 0, 0, 0 });
                response.AddRange(new byte[]
                {
                    0x00, 0x20, 0x00, 0x08, 0x00, 0x01, (byte)(xorPort >> 8), (byte)xorPort,
                    (byte)(address[0] ^ cookie[0]), (byte)(address[1] ^ cookie[1]),
                    (byte)(address[2] ^ cookie[2]), (byte)(address[3] ^ cookie[3]),
                });

                var bytes = response.ToArray();
                int length = bytes.Length - 20;
                bytes[2] = (byte)(length >> 8);
                bytes[3] = (byte)length;
                _ = server.SendToAsync(bytes, e.RemoteEndPoint);
            };
            return server;
        }

        [Fact]
        public async Task GetMappedEndPointAsync_ReturnsAddressSeenByServer()
        {
            using var server = CreateFakeStunServer();
            using var client = CreatePeer();

            var mapped = await StunClient.GetMappedEndPointAsync(client, server.LocalEndPoint!);

            Assert.Equal(client.LocalEndPoint, mapped);
        }

        [Fact]
        public async Task GetMappedEndPointAsync_ServerDoesNotAnswer_ReturnsNull()
        {
            using var silentServer = CreatePeer();
            using var client = CreatePeer();

            var mapped = await StunClient.GetMappedEndPointAsync(client, silentServer.LocalEndPoint!);

            Assert.Null(mapped);
        }

        [Fact]
        public async Task DiscoverAsync_SameMappingFromTwoServers_IsNotSymmetricNat()
        {
            using var server1 = CreateFakeStunServer();
            using var server2 = CreateFakeStunServer();
            using var client = CreatePeer();

            var result = await StunClient.DiscoverAsync(client, new[] { server1.LocalEndPoint!.ToString(), server2.LocalEndPoint!.ToString() });

            Assert.NotNull(result);
            Assert.Equal(client.LocalEndPoint, result!.PublicEndPoint);
            Assert.Equal(server1.LocalEndPoint, result.Server);
            Assert.False(result.IsSymmetricNat);
        }

        [Fact]
        public async Task DiscoverAsync_DifferentMappingPerServer_DetectsSymmetricNat()
        {
            using var server1 = CreateFakeStunServer();
            using var server2 = CreateFakeStunServer(seen => new IPEndPoint(seen.Address, seen.Port + 1)); // NAT выдал другой порт
            using var client = CreatePeer();

            var result = await StunClient.DiscoverAsync(client, new[] { server1.LocalEndPoint!.ToString(), server2.LocalEndPoint!.ToString() });

            Assert.NotNull(result);
            Assert.True(result!.IsSymmetricNat);
        }

        [Fact]
        public async Task DiscoverAsync_NoServerAnswers_ReturnsNull()
        {
            using var silentServer = CreatePeer();
            using var client = CreatePeer();

            var result = await StunClient.DiscoverAsync(client, new[] { silentServer.LocalEndPoint!.ToString() });

            Assert.Null(result);
        }
    }
}
