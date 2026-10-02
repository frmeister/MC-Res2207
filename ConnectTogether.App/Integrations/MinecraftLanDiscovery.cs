// ConnectTogether.App/Integrations/MinecraftLanDiscovery.cs

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ConnectTogether.App.Models;

namespace ConnectTogether.App.Integrations
{
    /// <summary>
    /// Поиск мира Minecraft, открытого для сети из одиночной игры.
    /// У такого мира случайный TCP-порт; игра раз в 1,5 с объявляет его в multicast-группе 224.0.2.60:4445
    /// сообщением «[MOTD]название[/MOTD][AD]порт[/AD]». Берём только объявления с этого компьютера.
    /// </summary>
    public static class MinecraftLanDiscovery
    {
        private static readonly IPAddress Group = IPAddress.Parse("224.0.2.60");
        private const int AnnouncePort = 4445;
        private static readonly TimeSpan ListenTime = TimeSpan.FromSeconds(2);
        private static readonly Regex PortPattern = new(@"\[AD\](\d{1,5})\[/AD\]", RegexOptions.Compiled);

        public static async Task<ServerEndpoint?> DiscoverAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ListenTime);

            try
            {
                using var udp = new UdpClient { ExclusiveAddressUse = false };
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, AnnouncePort));
                udp.JoinMulticastGroup(Group);

                var local = LocalAddresses();
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    if (!local.Contains(result.RemoteEndPoint.Address)) continue;

                    var match = PortPattern.Match(Encoding.UTF8.GetString(result.Buffer));
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int port) && port is > 0 and <= 65535)
                    {
                        Trace.WriteLine($"[MinecraftLanDiscovery] Found LAN world on TCP {port}");
                        return new ServerEndpoint(EndpointProtocol.Tcp, port);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // За отведённое время объявлений не было
            }
            catch (SocketException ex)
            {
                Trace.WriteLine($"[MinecraftLanDiscovery] Listening failed: {ex.Message}");
                return null;
            }
        }

        private static HashSet<IPAddress> LocalAddresses()
        {
            var set = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    set.Add(address.Address);
                }
            }
            return set;
        }
    }
}
