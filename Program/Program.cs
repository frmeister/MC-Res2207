// MinecraftTunnel.ConsoleHost/Program.cs

using System;
using System.Net;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using MCTunnel.Core.PublicIp;

namespace MinecraftTunnel.ConsoleHost
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Minecraft Tunnel Console Host");

            // Тест получения публичного IP
            try
            {
                var ipResolver = new PublicIpResolver();
                var publicIp = await ipResolver.GetPublicIpAddressAsync();
                Console.WriteLine($"Public IP: {publicIp}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not get public IP: {ex.Message}");
            }

            Console.WriteLine("Choose mode: 'host' or 'client'?");
            var mode = Console.ReadLine()?.ToLower();

            switch (mode)
            {
                case "host":
                    await RunAsHost();
                    break;
                case "client":
                    await RunAsClient();
                    break;
                default:
                    Console.WriteLine("Invalid mode. Exiting.");
                    break;
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        static async Task RunAsHost()
        {
            Console.WriteLine("Running as Host...");
            Console.WriteLine("Enter local port for UdpPeer (default 50000): ");
            if (!int.TryParse(Console.ReadLine(), out var port))
                port = 50000;

            using var peer = new UdpPeer(port);
            Console.WriteLine($"Listening on port {port}...");

            // Запускаем прием данных в фоне
            var receiveTask = Task.Run(async () => await peer.StartReceivingAsync());

            try
            {
                var clientEndPoint = await NatTraversal.WaitForClientAsync(peer);
                if (clientEndPoint != null)
                {
                    Console.WriteLine($"Connected to client at {clientEndPoint}");
                    // Здесь можно запустить TcpProxy и ReliableChannel
                    // Пока просто ждем
                    Console.WriteLine("Connected. Waiting for messages... Press Enter to stop.");
                    Console.ReadLine();
                }
                else
                {
                    Console.WriteLine("Failed to connect to client (timeout).");
                }
            }
            catch (TimeoutException tex)
            {
                Console.WriteLine(tex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Host setup failed: {ex.Message}");
            }
        }

        static async Task RunAsClient()
        {
            Console.WriteLine("Running as Client...");
            Console.Write("Enter host IP address: ");
            var hostIpStr = Console.ReadLine();
            if (!IPAddress.TryParse(hostIpStr, out var hostIp))
            {
                Console.WriteLine("Invalid IP address.");
                return;
            }
            Console.Write("Enter host port (default 50000): ");
            if (!int.TryParse(Console.ReadLine(), out var hostPort))
                hostPort = 50000;

            var hostEndPoint = new IPEndPoint(hostIp, hostPort);

            Console.WriteLine("Enter local port for UdpPeer (default 50001): ");
            if (!int.TryParse(Console.ReadLine(), out var port))
                port = 50001;

            using var peer = new UdpPeer(port);
            Console.WriteLine($"Using local port {port}...");

            // Запускаем прием данных в фоне
            var receiveTask = Task.Run(async () => await peer.StartReceivingAsync());

            try
            {
                bool connected = await NatTraversal.ConnectToHostAsync(peer, hostEndPoint);
                if (connected)
                {
                    Console.WriteLine($"Connected to host at {hostEndPoint}");
                    // Здесь можно запустить TcpProxy и ReliableChannel
                    // Пока просто ждем
                    Console.WriteLine("Connected. Waiting for messages... Press Enter to stop.");
                    Console.ReadLine();
                }
                else
                {
                    Console.WriteLine("Failed to connect to host (timeout).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client connection failed: {ex.Message}");
            }
        }
    }
}