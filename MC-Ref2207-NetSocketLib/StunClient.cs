// MCTunnel.Core.Network/StunClient.cs

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MCTunnel.Core.Network
{
    /// <summary>
    /// Результат опроса STUN-серверов.
    /// </summary>
    public sealed class StunResult
    {
        /// <summary>Внешний адрес (IP:порт), под которым NAT выпускает пакеты сокета, — его сообщают партнёру.</summary>
        public IPEndPoint PublicEndPoint { get; }

        /// <summary>STUN-сервер, который вернул PublicEndPoint.</summary>
        public IPEndPoint Server { get; }

        /// <summary>
        /// true — NAT выдаёт разный внешний адрес для разных получателей (симметричный NAT), пробой почти наверняка не сработает.
        /// null — ответил только один сервер, проверить не удалось.
        /// </summary>
        public bool? IsSymmetricNat { get; }

        public StunResult(IPEndPoint publicEndPoint, IPEndPoint server, bool? isSymmetricNat)
        {
            PublicEndPoint = publicEndPoint;
            Server = server;
            IsSymmetricNat = isSymmetricNat;
        }
    }

    /// <summary>
    /// Минимальный STUN-клиент (RFC 5389, только Binding Request).
    /// Запросы идут через тот же UdpPeer, что и пробой NAT: внешний порт привязан к конкретному сокету,
    /// и у другого сокета он был бы другим.
    /// </summary>
    public static class StunClient
    {
        public static readonly string[] DefaultServers =
        {
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
            "stun.cloudflare.com:3478",
            "stun.sipgate.net:3478",
        };

        private const int DefaultPort = 3478;
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccessResponse = 0x0101;
        private const ushort MappedAddressAttribute = 0x0001;
        private const ushort XorMappedAddressAttribute = 0x0020;
        private const uint MagicCookie = 0x2112A442;
        private const int HeaderSize = 20;
        private static readonly int[] RetryDelaysMs = { 250, 500, 1000 }; // Повтор с удвоением интервала, как в RFC 5389

        /// <summary>
        /// Опрашивает STUN-серверы по очереди, пока не ответят два разных: так заодно проверяется, не симметричный ли NAT.
        /// </summary>
        /// <returns>null, если не ответил ни один сервер (например, UDP заблокирован).</returns>
        public static async Task<StunResult?> DiscoverAsync(UdpPeer peer, IEnumerable<string>? servers = null, CancellationToken cancellationToken = default)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));

            IPEndPoint? firstMapped = null;
            IPEndPoint? firstServer = null;

            foreach (var server in servers ?? DefaultServers)
            {
                var serverEndPoint = await ResolveAsync(server, cancellationToken);
                if (serverEndPoint == null || serverEndPoint.Equals(firstServer)) continue;

                var mapped = await GetMappedEndPointAsync(peer, serverEndPoint, cancellationToken);
                if (mapped == null) continue;

                if (firstMapped == null)
                {
                    firstMapped = mapped;
                    firstServer = serverEndPoint;
                    continue;
                }

                bool symmetric = !mapped.Equals(firstMapped);
                Trace.WriteLine($"[Core.Network.Stun] {firstServer} sees {firstMapped}, {serverEndPoint} sees {mapped}, symmetric NAT={symmetric}");
                return new StunResult(firstMapped, firstServer!, symmetric);
            }

            return firstMapped == null ? null : new StunResult(firstMapped, firstServer!, null);
        }

        /// <summary>
        /// Отправляет Binding Request на сервер и возвращает внешний адрес сокета, который увидел сервер.
        /// Повторный запрос заодно не даёт NAT забыть маппинг, пока пользователь вводит адрес партнёра.
        /// </summary>
        /// <returns>null, если сервер не ответил.</returns>
        public static async Task<IPEndPoint?> GetMappedEndPointAsync(UdpPeer peer, IPEndPoint stunServer, CancellationToken cancellationToken = default)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (stunServer == null) throw new ArgumentNullException(nameof(stunServer));

            var transactionId = RandomNumberGenerator.GetBytes(12);
            var request = BuildBindingRequest(transactionId);
            var responseTcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
            {
                var mapped = ParseBindingResponse(e.Data, transactionId);
                if (mapped != null) responseTcs.TrySetResult(mapped);
            }

            peer.DataReceived += OnDataReceived;
            try
            {
                foreach (var delayMs in RetryDelaysMs)
                {
                    try
                    {
                        await peer.SendToAsync(request, stunServer);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        Trace.WriteLine($"[Core.Network.Stun] Failed to send request to {stunServer}: {ex.Message}");
                        return null;
                    }

                    try
                    {
                        var mapped = await responseTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                        Trace.WriteLine($"[Core.Network.Stun] {stunServer} -> mapped address {mapped}");
                        return mapped;
                    }
                    catch (TimeoutException)
                    {
                        // Запрос или ответ потерялся — повторяем
                    }
                }

                Trace.WriteLine($"[Core.Network.Stun] No response from {stunServer}");
                return null;
            }
            finally
            {
                peer.DataReceived -= OnDataReceived;
            }
        }

        private static async Task<IPEndPoint?> ResolveAsync(string server, CancellationToken cancellationToken)
        {
            string host = server;
            int port = DefaultPort;
            int colon = server.LastIndexOf(':');
            if (colon > 0 && int.TryParse(server.AsSpan(colon + 1), out var parsedPort))
            {
                host = server.Substring(0, colon);
                port = parsedPort;
            }

            try
            {
                // Только IPv4: UdpPeer слушает IPv4
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
                if (addresses.Length > 0) return new IPEndPoint(addresses[0], port);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                Trace.WriteLine($"[Core.Network.Stun] Failed to resolve {server}: {ex.Message}");
            }
            return null;
        }

        private static byte[] BuildBindingRequest(byte[] transactionId)
        {
            var request = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), BindingRequest);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Атрибутов нет
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
            transactionId.CopyTo(request, 8);
            return request;
        }

        private static IPEndPoint? ParseBindingResponse(byte[] data, byte[] transactionId)
        {
            if (data.Length < HeaderSize) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0)) != BindingSuccessResponse) return null;
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)) != MagicCookie) return null;
            if (!data.AsSpan(8, 12).SequenceEqual(transactionId)) return null; // Ответ на другой запрос

            int end = Math.Min(data.Length, HeaderSize + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
            IPEndPoint? mappedAddress = null;

            for (int pos = HeaderSize; pos + 4 <= end;)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
                int value = pos + 4;
                if (value + length > end) break;

                // Значение адреса: 1 байт резерв, 1 байт семейство (0x01 = IPv4), 2 байта порт, 4 байта IPv4
                if (length >= 8 && data[value + 1] == 0x01)
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(value + 2));
                    var address = data.AsSpan(value + 4, 4).ToArray();

                    if (type == XorMappedAddressAttribute)
                    {
                        // Порт и адрес передаются в XOR с magic cookie
                        port ^= (int)(MagicCookie >> 16);
                        Span<byte> cookie = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
                        for (int i = 0; i < 4; i++) address[i] ^= cookie[i];
                        return new IPEndPoint(new IPAddress(address), port);
                    }

                    if (type == MappedAddressAttribute)
                    {
                        // Старые серверы (RFC 3489) присылают только MAPPED-ADDRESS
                        mappedAddress ??= new IPEndPoint(new IPAddress(address), port);
                    }
                }

                pos = value + ((length + 3) & ~3); // Атрибуты выровнены по 4 байта
            }

            return mappedAddress;
        }
    }
}
