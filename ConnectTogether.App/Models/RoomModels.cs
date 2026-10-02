// ConnectTogether.App/Models/RoomModels.cs

namespace ConnectTogether.App.Models
{
    public enum PlayerStatus
    {
        Host,

        /// <summary>Вошёл, связь ещё не измерена.</summary>
        Joining,

        /// <summary>В комнате, игра не подключена.</summary>
        InRoom,

        /// <summary>Игра подключена к серверу хоста через ConnectTogether.</summary>
        InGame,

        Left,
    }

    /// <summary>Участник комнаты.</summary>
    public sealed class RoomPlayer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public PlayerStatus Status { get; set; }

        /// <summary>Пинг до хоста в миллисекундах; null — не измеряется (хост, отключившиеся).</summary>
        public int? PingMs { get; set; }

        /// <summary>Когда игрок отключился.</summary>
        public DateTime? LeftAt { get; set; }

        /// <summary>Это текущий пользователь.</summary>
        public bool IsYou { get; init; }
    }

    /// <summary>Сообщение в чате комнаты; системные («Вы подключились к комнате») показываются по центру.</summary>
    public sealed record ChatMessage(string Author, string Text, bool IsMine, bool IsSystem = false);
}
