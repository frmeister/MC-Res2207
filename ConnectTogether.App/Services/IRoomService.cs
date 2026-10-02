// ConnectTogether.App/Services/IRoomService.cs

using System.Net;
using ConnectTogether.App.Models;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Комнаты: создание хостом и вход игрока по адресу хоста IP:порт.
    /// Реализация — CoreRoomService поверх RoomHost/RoomClient из Core. События комнат приходят в потоке интерфейса.
    /// </summary>
    public interface IRoomService
    {
        /// <summary>
        /// Открывает комнату для сервера игры на этом компьютере.
        /// SocketException — порт комнаты занят другой программой.
        /// </summary>
        Task<IHostedRoom> CreateRoomAsync(GameIntegration game, ServerEndpoint server, string hostName, CancellationToken ct = default);

        /// <summary>
        /// Подключается к комнате по адресу хоста. Этапы сообщаются через progress.
        /// Ошибки подключения — <see cref="RoomJoinException"/>.
        /// </summary>
        Task<IJoinedRoom> JoinRoomAsync(string address, string playerName, IProgress<JoinProgress>? progress, CancellationToken ct = default);
    }

    public interface IRoom
    {
        /// <summary>Адрес комнаты IP:порт, который вводят друзья.</summary>
        string Address { get; }

        GameIntegration Game { get; }
        IReadOnlyList<RoomPlayer> Players { get; }

        event EventHandler? PlayersChanged;
        event EventHandler<ChatMessage>? MessageReceived;

        Task SendMessageAsync(string text);
    }

    /// <summary>Комната, которую открыл этот компьютер.</summary>
    public interface IHostedRoom : IRoom
    {
        ServerEndpoint Server { get; }
        DateTime OpenedAt { get; }
        int MaxPlayers { get; }

        /// <summary>UDP-порт комнаты на этом компьютере — его пробрасывают на роутере.</summary>
        int LocalPort { get; }

        /// <summary>Внешний IP (по STUN); null — определить не удалось.</summary>
        IPAddress? PublicAddress { get; }

        /// <summary>Адрес для друзей в той же локальной сети.</summary>
        string? LanAddress { get; }

        /// <summary>true — NAT меняет порт для каждого адресата, из интернета подключиться не получится без проброса порта.</summary>
        bool? IsSymmetricNat { get; }

        /// <summary>
        /// Впустить игрока по его внешнему адресу: некоторое время слать ему пакеты, чтобы роутер и брандмауэр
        /// этого компьютера пропустили его подключение. Игрок в это время должен подключаться.
        /// </summary>
        void LetIn(IPEndPoint player);

        /// <summary>Внешний адрес сменился — друзьям нужно отправить новый.</summary>
        event EventHandler? AddressChanged;

        Task CloseAsync();
    }

    /// <summary>Комната хоста, к которой подключился этот компьютер.</summary>
    public interface IJoinedRoom : IRoom
    {
        string HostName { get; }

        /// <summary>Локальный адрес, по которому игра подключается к серверу хоста через ConnectTogether; null — хост ещё не открыл порт.</summary>
        IPEndPoint? LocalEndPoint { get; }

        /// <summary>Пинг до хоста в миллисекундах; null — ещё не измерен.</summary>
        int? PingMs { get; }

        event EventHandler? PingChanged;
        event EventHandler? LocalEndPointChanged;

        /// <summary>Связь с хостом прервалась; интерфейс начинает автопереподключение.</summary>
        event EventHandler? ConnectionLost;

        /// <summary>Хост закрыл комнату.</summary>
        event EventHandler? Closed;

        Task<bool> ReconnectAsync(CancellationToken ct = default);
        Task LeaveAsync();
    }

    public enum JoinStage
    {
        FindingAddress,
        ContactingHost,
        Done,
    }

    /// <summary>
    /// Этап подключения; Room заполняется, когда хост ответил.
    /// MyAddress — свой внешний адрес IP:порт: его хост вводит в «Впустить по адресу».
    /// </summary>
    public sealed record JoinProgress(JoinStage Stage, RoomInfo? Room, string? MyAddress = null);

    public sealed record RoomInfo(string Address, GameIntegration Game, string HostName);

    public enum JoinFailure
    {
        /// <summary>Хост не ответил: адрес неверный, комната закрыта или сеть хоста не пропускает входящие подключения.</summary>
        HostNotResponding,

        /// <summary>Нет интернета или исходящий UDP заблокирован.</summary>
        NoInternet,

        RoomFull,
        IncompatibleVersion,
    }

    public sealed class RoomJoinException : Exception
    {
        public RoomJoinException(JoinFailure failure, IReadOnlyList<string> details, string? myAddress = null)
            : base(failure.ToString())
        {
            Failure = failure;
            Details = details;
            MyAddress = myAddress;
        }

        public JoinFailure Failure { get; }

        /// <summary>Свой внешний адрес, если его удалось узнать.</summary>
        public string? MyAddress { get; }

        /// <summary>Технические подробности для блока «Подробнее».</summary>
        public IReadOnlyList<string> Details { get; }
    }
}
