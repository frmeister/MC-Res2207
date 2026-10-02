// ConnectTogether.App/Models/GameIntegration.cs

using System.Net;

namespace ConnectTogether.App.Models
{
    public enum EndpointProtocol
    {
        Tcp,
        Udp,
    }

    /// <summary>Порт, на котором сервер игры принимает игроков.</summary>
    public sealed record ServerEndpoint(EndpointProtocol Protocol, int Port)
    {
        public override string ToString() => $"{(Protocol == EndpointProtocol.Tcp ? "TCP" : "UDP")} {Port}";
    }

    /// <summary>
    /// Как игроку зайти в игру через локальный адрес ConnectTogether.
    /// Либо клавиша консоли (Rust: F1 → client.connect …), либо путь по меню (Minecraft: Сетевая игра → Прямое подключение).
    /// </summary>
    public sealed class JoinInstruction
    {
        /// <summary>Клавиша, открывающая консоль игры, например F1.</summary>
        public string? ConsoleKey { get; init; }

        /// <summary>Пункты меню игры по порядку.</summary>
        public IReadOnlyList<string> MenuPath { get; init; } = Array.Empty<string>();

        /// <summary>Что ввести в игре; {address} заменяется на локальный адрес.</summary>
        public string Template { get; init; } = "{address}";

        public string CopyLabel { get; init; } = "Скопировать адрес";

        public string Format(IPEndPoint address) => Template.Replace("{address}", address.ToString());
    }

    /// <summary>
    /// Описание интеграции игры: всё игро-специфичное, что нужно интерфейсу.
    /// Экраны не знают про конкретные игры и берут тексты, порты и подсказки только отсюда.
    /// </summary>
    public sealed class GameIntegration
    {
        public required string Id { get; init; }
        public required string Name { get; init; }

        /// <summary>Редакция или издатель под названием: «Java Edition», «Facepunch Studios».</summary>
        public required string Subtitle { get; init; }

        /// <summary>Имя иконки из набора интерфейса.</summary>
        public required string Icon { get; init; }

        public required string Version { get; init; }
        public required string Author { get; init; }

        /// <summary>Подсказка на карточке выбора игры.</summary>
        public required string SelectionHint { get; init; }

        /// <summary>Подпись в списке интеграций; по умолчанию — Subtitle.</summary>
        public string? CatalogSubtitle { get; init; }

        public string ListSubtitle => CatalogSubtitle ?? Subtitle;

        /// <summary>Полное название сервера: «Rust Dedicated Server».</summary>
        public required string ServerTitle { get; init; }

        /// <summary>Короткое название сервера для карточки «Игра» в комнате хоста.</summary>
        public required string ServerShortName { get; init; }

        /// <summary>Порты, по которым проверяем, что сервер запущен.</summary>
        public required IReadOnlyList<ServerEndpoint> Endpoints { get; init; }

        /// <summary>Имена процессов сервера без .exe — только для «Подробнее».</summary>
        public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();

        /// <summary>Шаги «как запустить сервер», если он не найден.</summary>
        public required IReadOnlyList<string> StartServerSteps { get; init; }

        /// <summary>Советы, если сервер перестал отвечать при открытой комнате.</summary>
        public required IReadOnlyList<string> ServerStoppedTips { get; init; }

        public required string InstallHelpLabel { get; init; }
        public required string InstallHelpUrl { get; init; }

        public required JoinInstruction Join { get; init; }

        /// <summary>
        /// Дополнительный поиск сервера, который не описать портами (низкоуровневая часть интеграции).
        /// Возвращает порт найденного сервера или null.
        /// </summary>
        public Func<CancellationToken, Task<ServerEndpoint?>>? DiscoverAsync { get; init; }
    }
}
