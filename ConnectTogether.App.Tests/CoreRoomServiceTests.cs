// ConnectTogether.App.Tests/CoreRoomServiceTests.cs

using System.IO;
using System.Net;
using ConnectTogether.App.Integrations;
using ConnectTogether.App.Models;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.Tests
{
    /// <summary>
    /// Настоящие комнаты Core через сервисный слой приложения: хост и игрок на 127.0.0.1.
    /// </summary>
    public class CoreRoomServiceTests : IDisposable
    {
        private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"ct-core-{Guid.NewGuid():N}.json");
        private readonly CoreRoomService _service;

        public CoreRoomServiceTests()
        {
            var settings = new SettingsStore(_settingsPath);
            settings.Update(s => s.AppPort = 0); // Любой свободный порт — тесты не мешают запущенному приложению
            _service = new CoreRoomService(settings, IntegrationCatalog.CreateBuiltIn(), IPAddress.Loopback, discoverPublicAddress: false);
        }

        public void Dispose() => File.Delete(_settingsPath);

        private static async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Не дождались: {what}");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task HostAndPlayer_SeeEachOther_Chat_AndLeave()
        {
            var hosted = await _service.CreateRoomAsync(IntegrationCatalog.Rust, new ServerEndpoint(EndpointProtocol.Udp, 28015), "Лёша");
            var progress = new List<JoinStage>();
            var joined = await _service.JoinRoomAsync($"127.0.0.1:{hosted.LocalPort}", "Марина",
                new SyncProgress<JoinProgress>(p => { lock (progress) progress.Add(p.Stage); }));

            Assert.Equal("Лёша", joined.HostName);
            Assert.Equal("rust", joined.Game.Id);
            lock (progress) Assert.Contains(JoinStage.Done, progress);
            Assert.NotNull(joined.LocalEndPoint); // Порт сервера Rust открыт у игрока
            Assert.Equal(IPAddress.Loopback, joined.LocalEndPoint!.Address);

            await WaitUntilAsync(() => hosted.Players.Count == 2 && joined.Players.Count == 2, "оба видят двоих");
            Assert.True(joined.Players.Single(p => p.Name == "Марина").IsYou);
            Assert.True(hosted.Players.Single(p => p.Name == "Лёша").IsYou);

            var inbox = new List<ChatMessage>();
            hosted.MessageReceived += (_, m) => { lock (inbox) inbox.Add(m); };
            await joined.SendMessageAsync("Я на месте");
            await WaitUntilAsync(() => { lock (inbox) return inbox.Count == 1; }, "сообщение игрока у хоста");
            Assert.Equal("Марина", inbox[0].Author);

            await WaitUntilAsync(() => joined.PingMs != null, "пинг у игрока");

            await joined.LeaveAsync();
            await WaitUntilAsync(() => hosted.Players.Single(p => p.Name == "Марина").Status == PlayerStatus.Left, "игрок вышел");
            await hosted.CloseAsync();
        }

        /// <summary>IProgress без SynchronizationContext: вызывается сразу, порядок этапов сохраняется.</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }
}
