// ConnectTogether.App/Integrations/IntegrationCatalog.cs

using ConnectTogether.App.Models;

namespace ConnectTogether.App.Integrations
{
    /// <summary>
    /// Установленные интеграции. Пока это две эталонные интеграции MVP — Minecraft и Rust.
    /// Когда появится SDK, сюда добавятся интеграции, загруженные из файлов; экраны при этом не меняются.
    /// </summary>
    public sealed class IntegrationCatalog
    {
        public IntegrationCatalog(IEnumerable<GameIntegration> integrations)
        {
            All = integrations.ToList();
        }

        public IReadOnlyList<GameIntegration> All { get; }

        public GameIntegration? Find(string? id) => All.FirstOrDefault(i => i.Id == id);

        public static IntegrationCatalog CreateBuiltIn() => new(new[] { Minecraft, Rust });

        /// <summary>Игра хоста, интеграции которой у игрока нет: показываем только адрес для подключения.</summary>
        public static GameIntegration Unknown(string id) => new()
        {
            Id = id,
            Name = string.IsNullOrEmpty(id) ? "Игра" : id,
            Subtitle = "Интеграция не установлена",
            Icon = "puzzle",
            Version = "—",
            Author = "—",
            SelectionHint = "",
            ServerTitle = id,
            ServerShortName = id,
            Endpoints = Array.Empty<ServerEndpoint>(),
            StartServerSteps = Array.Empty<string>(),
            ServerStoppedTips = Array.Empty<string>(),
            InstallHelpLabel = "",
            InstallHelpUrl = Services.AppLinks.IntegrationGuide,
            Join = new JoinInstruction { Template = "{address}", CopyLabel = "Скопировать адрес" },
        };

        public static readonly GameIntegration Minecraft = new()
        {
            Id = "minecraft",
            Name = "Minecraft",
            Subtitle = "Java Edition",
            Icon = "cube",
            Version = "1.2.0",
            Author = "Команда ConnectTogether",
            SelectionHint = "Подойдёт обычный сервер или мир, открытый для сети из одиночной игры.",
            ServerTitle = "Сервер Minecraft",
            ServerShortName = "Minecraft Java",
            Endpoints = new[] { new ServerEndpoint(EndpointProtocol.Tcp, 25565) },
            StartServerSteps = new[]
            {
                "Запустите сервер Minecraft или откройте мир для сети: Esc → «Открыть для сети» → «Открыть мир для сети».",
                "Если это отдельный сервер, дождитесь в его окне строки «Done» — обычно это меньше минуты.",
                "Вернитесь сюда и нажмите «Проверить снова».",
            },
            ServerStoppedTips = new[]
            {
                "Проверьте, не закрылось ли окно сервера Minecraft и открыт ли мир для сети.",
                "Запустите сервер снова — комната и код останутся прежними.",
                "Нажмите «Проверить снова».",
            },
            InstallHelpLabel = "Как запустить сервер Minecraft",
            InstallHelpUrl = "https://www.minecraft.net/download/server",
            Join = new JoinInstruction
            {
                MenuPath = new[] { "Сетевая игра", "Прямое подключение" },
                Template = "{address}",
                CopyLabel = "Скопировать адрес",
            },
            DiscoverAsync = MinecraftLanDiscovery.DiscoverAsync,
        };

        public static readonly GameIntegration Rust = new()
        {
            Id = "rust",
            Name = "Rust",
            Subtitle = "Facepunch Studios",
            Icon = "axe",
            Version = "1.0.3",
            Author = "Команда ConnectTogether",
            SelectionHint = "Нужен запущенный Rust Dedicated Server — его можно скачать через SteamCMD.",
            CatalogSubtitle = "Rust Dedicated Server",
            ServerTitle = "Rust Dedicated Server",
            ServerShortName = "Rust Dedicated",
            Endpoints = new[] { new ServerEndpoint(EndpointProtocol.Udp, 28015) },
            ProcessNames = new[] { "RustDedicated" },
            StartServerSteps = new[]
            {
                "Запустите RustDedicated.exe или ваш bat-файл запуска сервера.",
                "Дождитесь в окне сервера строки «Server startup complete» — обычно это 1–3 минуты.",
                "Вернитесь сюда и нажмите «Проверить снова».",
            },
            ServerStoppedTips = new[]
            {
                "Проверьте, не закрылось ли окно RustDedicated.exe.",
                "Запустите сервер снова — комната и код останутся прежними.",
                "Нажмите «Проверить снова».",
            },
            InstallHelpLabel = "Как установить сервер Rust",
            InstallHelpUrl = "https://wiki.facepunch.com/rust/Creating-a-server",
            Join = new JoinInstruction
            {
                ConsoleKey = "F1",
                Template = "client.connect {address}",
                CopyLabel = "Скопировать",
            },
        };
    }
}
