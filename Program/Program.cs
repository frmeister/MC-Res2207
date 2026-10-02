// Program/Program.cs
// Простой ручной тест: NAT traversal + ReliableChannel между двумя реальными машинами.
//
// Как использовать:
// 1. Запустить программу на ОБЕИХ машинах.
// 2. Каждая сторона увидит свой публичный IP и спросит локальный UDP-порт.
//    Сообщите друг другу (голосом/в чате) пару "ваш публичный IP : выбранный порт".
// 3. Одна сторона выбирает режим "host", вторая — "client".
//    "client" вводит IP:порт стороны "host".
// 4. Если NAT пробит — откроется простой текстовый чат через ReliableChannel.
//
// ВАЖНО (ограничения текущей реализации):
// - Это НЕ STUN: если ваш роутер подменяет внешний порт (symmetric NAT),
//   punching может не сработать — тогда нужен проброс порта на роутере
//   на заранее известный localPort.
// - Проверьте, что localPort не блокируется файрволом (разрешить исходящий
//   и входящий UDP на этот порт).

using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using MCTunnel.Core.PublicIp;
using MC_Ref2207_NetSocketLib;

namespace MinecraftTunnel.ConsoleHost
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== MC Tunnel: тест P2P через NAT ===");

            // 1. Узнаём свой публичный IP — его нужно продиктовать второй стороне
            try
            {
                var ipResolver = new PublicIpResolver();
                var publicIp = await ipResolver.GetPublicIpAddressAsync();
                Console.WriteLine($"Ваш публичный IP: {publicIp}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось получить публичный IP: {ex.Message}");
            }

            Console.Write("Локальный UDP-порт (например 50000): ");
            if (!int.TryParse(Console.ReadLine(), out var localPort))
                localPort = 50000;

            Console.WriteLine($"Сообщите второй стороне: ВАШ_ПУБЛИЧНЫЙ_IP:{localPort}");
            Console.WriteLine();

            using var peer = new UdpPeer(localPort);
            _ = Task.Run(() => peer.StartReceivingAsync());

            Console.Write("Кто вы — host или client? (host/client): ");
            var mode = Console.ReadLine()?.Trim().ToLower();

            IPEndPoint? remoteEndPoint;

            try
            {
                if (mode == "host")
                {
                    Console.WriteLine("Ожидаем подключение клиента...");
                    remoteEndPoint = await NatTraversal.WaitForClientAsync(peer);
                }
                else if (mode == "client")
                {
                    Console.Write("IP второй стороны: ");
                    var ipStr = Console.ReadLine();
                    Console.Write("Порт второй стороны: ");
                    int.TryParse(Console.ReadLine(), out var remotePort);

                    if (!IPAddress.TryParse(ipStr, out var remoteIp))
                    {
                        Console.WriteLine("Некорректный IP.");
                        return;
                    }

                    remoteEndPoint = new IPEndPoint(remoteIp, remotePort);
                    bool punched = await NatTraversal.ConnectToHostAsync(peer, remoteEndPoint);
                    if (!punched)
                    {
                        Console.WriteLine("Не удалось пробить NAT (timeout).");
                        return;
                    }
                }
                else
                {
                    Console.WriteLine("Неизвестный режим.");
                    return;
                }
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Timeout: вторая сторона не ответила за отведённое время.");
                return;
            }

            if (remoteEndPoint == null)
            {
                Console.WriteLine("Не удалось установить соединение.");
                return;
            }

            Console.WriteLine($"NAT пробит, партнёр: {remoteEndPoint}");

            // 2. Поднимаем надёжный канал поверх пробитого UDP-соединения
            using var channel = new ReliableChannel(peer);
            channel.DataReceived += (_, data) =>
            {
                Console.WriteLine($"\n[Партнёр]: {Encoding.UTF8.GetString(data)}");
                Console.Write("> ");
            };

            // Connect() сейчас синхронный и блокирующий (busy-wait до 5 сек),
            // поэтому уводим его в отдельный поток, чтобы не морозить консоль.
            bool connected = await Task.Run(() =>
            {
                try
                {
                    channel.ConnectAsync(remoteEndPoint);
                    return channel.State == ConnectionState.Established;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка установления ReliableChannel: {ex.Message}");
                    return false;
                }
            });

            if (!connected)
            {
                Console.WriteLine("ReliableChannel не установился (Hello от партнёра не пришёл).");
                return;
            }

            Console.WriteLine("Канал установлен. Пишите сообщения, Ctrl+C — выход.\n");

            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    await channel.SendDataAsync(Encoding.UTF8.GetBytes(line));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка отправки: {ex.Message}");
                }
            }
        }
    }
}