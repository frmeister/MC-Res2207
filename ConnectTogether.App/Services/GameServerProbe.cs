// ConnectTogether.App/Services/GameServerProbe.cs

using System.Diagnostics;
using System.Net.NetworkInformation;
using ConnectTogether.App.Models;

namespace ConnectTogether.App.Services
{
    public sealed record ServerProbeResult(bool Found, ServerEndpoint? Endpoint, IReadOnlyList<string> Details);

    public interface IGameServerProbe
    {
        /// <summary>Проверяет, запущен ли на этом компьютере сервер игры из интеграции.</summary>
        Task<ServerProbeResult> ProbeAsync(GameIntegration game, CancellationToken ct = default);

        /// <summary>Работает ли ещё сервер на найденном порту — для наблюдения из открытой комнаты.</summary>
        Task<bool> IsRunningAsync(ServerEndpoint endpoint, CancellationToken ct = default);
    }

    /// <summary>
    /// Проверка сервера без знания протокола игры: слушает ли кто-то порты из интеграции.
    /// Если не слушает — запускается дополнительный поиск интеграции (например, мир Minecraft, открытый для сети).
    /// </summary>
    public sealed class LocalServerProbe : IGameServerProbe
    {
        public async Task<ServerProbeResult> ProbeAsync(GameIntegration game, CancellationToken ct = default)
        {
            var details = new List<string>();
            ServerEndpoint? found = null;

            foreach (var endpoint in game.Endpoints)
            {
                bool listening = IsListening(endpoint);
                details.Add($"Проверка: {endpoint} — {(listening ? "порт занят сервером" : "никто не слушает")}");
                found ??= listening ? endpoint : null;
            }

            foreach (var name in game.ProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                details.Add(processes.Length > 0
                    ? $"Процесс {name}.exe — запущен (PID {processes[0].Id})"
                    : $"Процесс {name}.exe — не найден");
                foreach (var p in processes) p.Dispose();
            }

            if (found == null && game.DiscoverAsync != null)
            {
                found = await game.DiscoverAsync(ct);
                details.Add(found != null ? $"Найден мир, открытый для сети: {found}" : "Мир, открытый для сети, — не найден");
            }

            details.Add($"Интеграция: {game.Id} {game.Version}");
            Trace.WriteLine($"[LocalServerProbe] {game.Id}: {(found != null ? found.ToString() : "not found")}");
            return new ServerProbeResult(found != null, found, details);
        }

        public Task<bool> IsRunningAsync(ServerEndpoint endpoint, CancellationToken ct = default) =>
            Task.FromResult(IsListening(endpoint));

        private static bool IsListening(ServerEndpoint endpoint)
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = endpoint.Protocol == EndpointProtocol.Udp
                ? properties.GetActiveUdpListeners()
                : properties.GetActiveTcpListeners();
            return listeners.Any(l => l.Port == endpoint.Port);
        }
    }
}
