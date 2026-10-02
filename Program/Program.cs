// Program/Program.cs
// Простой ручной тест: пробой NAT + ReliableChannel между двумя реальными машинами.
//
// Как использовать:
// 1. Запустить программу на ОБЕИХ машинах. Если Windows спросит про брандмауэр — разрешить доступ.
// 2. Каждая сторона увидит свой внешний адрес "IP:порт" (его сообщает STUN-сервер).
//    Обменяйтесь адресами (голосом/в чате) и введите адрес партнёра.
// 3. Обе стороны одновременно шлют друг другу пакеты (UDP hole punching): исходящий пакет открывает
//    в своём NAT проход для встречных. Нажимать Enter точно одновременно не нужно — попытка длится 30 секунд.
// 4. Если NAT пробит — откроется простой текстовый чат через ReliableChannel.
//
// Пересылка портов игрового сервера (например, Rust или Minecraft):
// - тот, у кого запущен сервер, на вопрос про порты вводит udp:28015 (Rust) или tcp:25565 (Minecraft);
// - у партнёра откроется свободный порт на 127.0.0.1, и программа подскажет, куда подключаться в игре
//   (Rust: F1 → client.connect 127.0.0.1:<порт>, Minecraft: Прямое подключение → 127.0.0.1:<порт>).
// Программа должна работать у обоих всё время игры.
//
// Для проверки на одном компьютере запустите две копии с разными портами и вводите 127.0.0.1:<порт другой копии>.
// Отладочный лог каждого запуска пишется в папку logs рядом с программой.
//
// ВАЖНО (ограничения текущей реализации):
// - Если у ОБЕИХ сторон симметричный NAT (программа предупредит), внешний порт меняется для каждого адресата
//   и пробой не сработает. Тогда нужен проброс порта на роутере на выбранный локальный порт.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCTunnel.Core.Network;
using MCTunnel.Core.PublicIp;
using MC_Ref2207_NetSocketLib;

namespace MinecraftTunnel.ConsoleHost
{
    class Program
    {
        private const int DefaultLocalPort = 50000;
        private const int PunchTimeoutSeconds = 30;
        private const int HandshakeTimeoutMs = 10000;
        private static readonly TimeSpan MappingKeepAliveInterval = TimeSpan.FromSeconds(15);

        static async Task Main(string[] args)
        {
            DebugFileLogger.Start();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Trace.WriteLine($"[Program] Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Непредвиденная ошибка: {ex.Message}");
                Trace.WriteLine($"[Program] Fatal error: {ex}");
            }
            finally
            {
                if (DebugFileLogger.LogFilePath != null)
                {
                    Console.WriteLine($"Отладочный лог: {DebugFileLogger.LogFilePath}");
                }
                DebugFileLogger.Stop();

                // Без паузы окно, открытое двойным щелчком, закрывается сразу и сообщение не прочитать
                Console.WriteLine("Нажмите Enter, чтобы закрыть окно...");
                Console.ReadLine();
            }
        }

        private static async Task RunAsync()
        {
            Console.WriteLine("=== MC Tunnel: тест P2P через NAT ===");

            Console.Write($"Локальный UDP-порт (Enter — {DefaultLocalPort}): ");
            if (!int.TryParse(Console.ReadLine(), out var localPort) || localPort < 1 || localPort > IPEndPoint.MaxPort)
                localPort = DefaultLocalPort;

            using var peer = TryOpenPeer(localPort);
            if (peer == null) return;
            _ = Task.Run(() => peer.StartReceivingAsync());
            Trace.WriteLine($"[Program] UDP socket opened on {peer.LocalEndPoint}");

            // 1. Узнаём внешний адрес сокета — его нужно продиктовать второй стороне
            var stun = await DiscoverPublicAddressAsync(peer, localPort);
            var shares = ReadShares();

            // 2. Пока люди обмениваются адресами, не даём NAT забыть маппинг
            var keepAliveCts = new CancellationTokenSource();
            _ = stun != null ? KeepMappingAliveAsync(peer, stun, keepAliveCts.Token) : Task.CompletedTask;

            // 3. Пробиваем NAT, пока не получится или пользователь не откажется
            IPEndPoint? remoteEndPoint = null;
            try
            {
                IPEndPoint? partner = null;
                while (remoteEndPoint == null)
                {
                    partner = ReadPartnerEndPoint(partner);
                    if (partner == null) return;

                    Console.WriteLine($"Пробиваем NAT к {partner} (до {PunchTimeoutSeconds} с). Партнёр должен в это время ввести ваш адрес...");
                    remoteEndPoint = await NatTraversal.PunchAsync(peer, partner, TimeSpan.FromSeconds(PunchTimeoutSeconds));

                    if (remoteEndPoint == null)
                    {
                        Console.WriteLine("Партнёр не ответил. Проверьте, что адреса введены верно и программа партнёра тоже пробивает NAT.");
                    }
                }
            }
            finally
            {
                keepAliveCts.Cancel();
            }

            Console.WriteLine($"NAT пробит, партнёр: {remoteEndPoint}");

            // 4. Поднимаем надёжный канал поверх пробитого UDP-соединения, поверх него — чат и пересылку портов
            using var channel = new ReliableChannel(peer);
            using var session = new TunnelSession(channel);
            session.TextReceived += (_, text) =>
            {
                Console.WriteLine($"\n[Партнёр]: {text}");
                Console.Write("> ");
            };
            session.RemotePortOpened += (_, port) =>
            {
                Console.WriteLine($"\nПартнёр открыл {port.RemoteRule}: подключайтесь в игре к {port.LocalEndPoint}");
                // Без стрелок "→": консоль Windows в кодировке cp866 их не показывает
                Console.WriteLine(port.RemoteRule.Protocol == ForwardProtocol.Udp
                    ? $"  Например, в Rust: F1, затем client.connect {port.LocalEndPoint}"
                    : $"  Например, в Minecraft: Сетевая игра > Прямое подключение > {port.LocalEndPoint}");
                Console.Write("> ");
            };
            channel.Closed += (_, _) => Console.WriteLine("\nСоединение с партнёром закрыто. Нажмите Enter.");

            bool connected;
            try
            {
                connected = await channel.ConnectAsync(remoteEndPoint, HandshakeTimeoutMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка установления ReliableChannel: {ex.Message}");
                Trace.WriteLine($"[Program] ReliableChannel.ConnectAsync failed: {ex}");
                connected = false;
            }

            if (!connected)
            {
                Console.WriteLine("ReliableChannel не установился (Hello от партнёра не пришёл).");
                return;
            }

            if (shares.Count > 0)
            {
                await session.ShareAsync(shares);
                Console.WriteLine($"Партнёру открыты порты: {string.Join(", ", shares)}.");
            }

            Console.WriteLine("Соединение установлено. Не закрывайте программу, пока идёт игра.");
            Console.WriteLine("Здесь можно писать сообщения партнёру, Ctrl+C — выход.\n");

            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line == null) break; // Ввод закрыт (Ctrl+Z / конец перенаправленного ввода)
                if (line.Length == 0) continue;

                if (channel.State != ConnectionState.Established)
                {
                    Console.WriteLine("Соединение закрыто (партнёр не отвечает).");
                    break;
                }

                try
                {
                    await session.SendTextAsync(line);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка отправки: {ex.Message}");
                    Trace.WriteLine($"[Program] SendDataAsync failed: {ex}");
                }
            }
        }

        // Порты своих серверов, которые откроем партнёру
        private static List<ForwardRule> ReadShares()
        {
            while (true)
            {
                Console.WriteLine("Какие порты вашего сервера открыть партнёру? Например: udp:28015 — Rust, tcp:25565 — Minecraft.");
                Console.Write("Несколько — через запятую, Enter — никакие: ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) return new List<ForwardRule>();

                var rules = new List<ForwardRule>();
                string? invalid = null;
                foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ForwardRule.TryParse(part, out var rule)) rules.Add(rule);
                    else invalid ??= part;
                }

                if (invalid != null)
                {
                    Console.WriteLine($"Не понял \"{invalid}\": нужно в виде udp:28015 или tcp:25565.");
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (rule.IsLocalPortFree())
                    {
                        Console.WriteLine($"Внимание: на {rule} сейчас ничего не запущено. Запустите сервер, иначе партнёр не подключится.");
                    }
                }

                Trace.WriteLine($"[Program] Ports to share: {string.Join(", ", rules)}");
                Console.WriteLine();
                return rules;
            }
        }

        private static async Task<StunResult?> DiscoverPublicAddressAsync(UdpPeer peer, int localPort)
        {
            Console.WriteLine("Определяем внешний адрес через STUN...");
            var stun = await StunClient.DiscoverAsync(peer);

            if (stun != null)
            {
                Trace.WriteLine($"[Program] STUN: public endpoint {stun.PublicEndPoint}, symmetric NAT={stun.IsSymmetricNat}");
                Console.WriteLine($"Ваш адрес для партнёра: {stun.PublicEndPoint}");
                if (stun.IsSymmetricNat == true)
                {
                    Console.WriteLine("ВНИМАНИЕ: у вас симметричный NAT — внешний порт меняется для каждого адресата.");
                    Console.WriteLine($"Если у партнёра NAT тоже симметричный, пробой не сработает: нужен проброс UDP-порта {localPort} на роутере.");
                }
                Console.WriteLine();
                return stun;
            }

            // STUN не ответил — скорее всего, исходящий UDP блокируется. Показываем хотя бы IP
            Trace.WriteLine("[Program] STUN: no server answered");
            Console.WriteLine("STUN-серверы не ответили: возможно, UDP блокируется брандмауэром или провайдером.");
            try
            {
                var publicIp = await new PublicIpResolver().GetPublicIpAddressAsync();
                Console.WriteLine($"Ваш публичный IP: {publicIp}. Внешний порт неизвестен — сообщите партнёру {publicIp}:{localPort},");
                Console.WriteLine($"но сработает это только при пробросе UDP-порта {localPort} на роутере.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось получить публичный IP: {ex.Message}");
                Trace.WriteLine($"[Program] PublicIpResolver failed: {ex}");
            }
            Console.WriteLine();
            return null;
        }

        // NAT забывает неиспользуемый UDP-маппинг (бывает уже через 30 с), и внешний порт сменится,
        // пока люди диктуют друг другу адреса. Периодический STUN-запрос держит маппинг и проверяет адрес.
        private static async Task KeepMappingAliveAsync(UdpPeer peer, StunResult stun, CancellationToken cancellationToken)
        {
            var current = stun.PublicEndPoint;
            try
            {
                while (true)
                {
                    await Task.Delay(MappingKeepAliveInterval, cancellationToken);
                    var mapped = await StunClient.GetMappedEndPointAsync(peer, stun.Server, cancellationToken);
                    if (mapped != null && !mapped.Equals(current))
                    {
                        current = mapped;
                        Trace.WriteLine($"[Program] Public endpoint changed to {mapped}");
                        Console.WriteLine($"\nВНИМАНИЕ: ваш внешний адрес изменился на {mapped}. Сообщите партнёру новый адрес.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // NAT пробит или программа завершается
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Program] Mapping keep-alive failed: {ex}");
            }
        }

        // Возвращает null, если пользователь вышел (q) или ввод закрыт
        private static IPEndPoint? ReadPartnerEndPoint(IPEndPoint? previous)
        {
            while (true)
            {
                Console.Write(previous == null
                    ? "Адрес партнёра (IP:порт, q — выход): "
                    : $"Адрес партнёра (IP:порт, Enter — снова {previous}, q — выход): ");

                var input = Console.ReadLine()?.Trim();
                if (input == null || input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                if (input.Length == 0 && previous != null) return previous;

                if (IPEndPoint.TryParse(input, out var endPoint) &&
                    endPoint.AddressFamily == AddressFamily.InterNetwork && endPoint.Port > 0)
                {
                    Trace.WriteLine($"[Program] Partner endpoint entered: {endPoint}");
                    return endPoint;
                }

                Console.WriteLine("Нужен адрес вида 203.0.113.5:50000.");
            }
        }

        private static UdpPeer? TryOpenPeer(int localPort)
        {
            try
            {
                return new UdpPeer(localPort);
            }
            catch (SocketException ex)
            {
                // Чаще всего порт уже занят другой программой
                Console.WriteLine($"Не удалось открыть UDP-порт {localPort}: {ex.Message}");
                Trace.WriteLine($"[Program] Failed to open UDP port {localPort}: {ex}");
                return null;
            }
        }

        // Исключение в фоновом потоке завершает процесс — записываем его и не даём окну закрыться молча
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Trace.WriteLine($"[Program] Unhandled exception: {e.ExceptionObject}");
            Console.WriteLine($"Критическая ошибка: {(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject}");
            if (DebugFileLogger.LogFilePath != null)
            {
                Console.WriteLine($"Подробности в логе: {DebugFileLogger.LogFilePath}");
            }
            DebugFileLogger.Stop();
            Console.WriteLine("Нажмите Enter, чтобы закрыть окно...");
            Console.ReadLine();
        }
    }
}
