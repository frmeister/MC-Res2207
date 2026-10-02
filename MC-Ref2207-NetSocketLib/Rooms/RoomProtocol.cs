// MCTunnel.Core.Network/Rooms/RoomProtocol.cs

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace MCTunnel.Core.Rooms
{
    public enum RoomMemberStatus : byte
    {
        /// <summary>Хост комнаты.</summary>
        Host = 0,

        /// <summary>Вошёл в комнату, связь ещё не измерена.</summary>
        Joining = 1,

        /// <summary>В комнате, игра через туннель не подключена.</summary>
        InRoom = 2,

        /// <summary>Игра участника подключена к серверу хоста через туннель.</summary>
        InGame = 3,

        /// <summary>Вышел или потерял связь.</summary>
        Left = 4,
    }

    /// <summary>Участник комнаты. Id хоста — 0.</summary>
    public sealed record RoomMember(int Id, string Name, RoomMemberStatus Status, int? PingMs, DateTime? LeftAt = null);

    /// <summary>Сообщение чата комнаты.</summary>
    public sealed record RoomChatMessage(int AuthorId, string AuthorName, string Text);

    public enum RoomRejectReason : byte
    {
        RoomFull = 1,
        IncompatibleVersion = 2,
    }

    /// <summary>Что хост сообщает игроку при входе. ShareCount — сколько портов серверов хост откроет следом.</summary>
    public sealed record RoomWelcome(int MemberId, int MaxPlayers, string GameId, string HostName, int ShareCount);

    /// <summary>
    /// Кадры протокола комнаты. Ходят по тому же ReliableChannel, что и кадры TunnelSession (пересылка портов),
    /// и отличаются первым байтом: комнате отведены 0x30–0x3F, остальное принадлежит TunnelSession.
    /// Надёжные кадры (вход, состав, чат) — через SendDataAsync, пинг — датаграммами: потерянный замер
    /// не нужно повторять, а повтор исказил бы время.
    /// </summary>
    public static class RoomProtocol
    {
        /// <summary>Версия протокола: хост не пускает игроков с другой.</summary>
        public const byte Version = 1;

        public const int MaxNameLength = 32;
        public const int MaxChatLength = 500;

        public const byte FirstFrame = 0x30;
        public const byte LastFrame = 0x3F;

        public const byte Join = 0x30;       // игрок → хост: [версия][имя]
        public const byte Welcome = 0x31;    // хост → игрок: [версия][id(2)][макс. игроков][игра][имя хоста][число портов]
        public const byte Reject = 0x32;     // хост → игрок: [причина]
        public const byte Roster = 0x33;     // хост → игроки: [N] × ([id(2)][статус][пинг(2)][имя])
        public const byte ChatSend = 0x34;   // игрок → хост: [текст]
        public const byte Chat = 0x35;       // хост → игроки: [id автора(2)][имя автора][текст]
        public const byte Leave = 0x36;      // игрок → хост: выхожу
        public const byte Closed = 0x37;     // хост → игроки: комната закрыта
        public const byte Ping = 0x38;       // датаграмма: [номер(4)][метка времени отправителя(8)]
        public const byte Pong = 0x39;       // датаграмма: эхо Ping

        private const ushort NoPing = ushort.MaxValue;

        public static bool IsRoomFrame(byte[] frame) => frame.Length > 0 && frame[0] >= FirstFrame && frame[0] <= LastFrame;

        /// <summary>Обрезает имя до MaxNameLength символов и убирает переводы строк.</summary>
        public static string CleanName(string name)
        {
            var clean = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (clean.Length > MaxNameLength) clean = clean.Substring(0, MaxNameLength);
            return clean.Length == 0 ? "Игрок" : clean;
        }

        public static string CleanChat(string text)
        {
            var clean = text.Trim();
            return clean.Length > MaxChatLength ? clean.Substring(0, MaxChatLength) : clean;
        }

        // -- Сборка кадров --

        public static byte[] BuildJoin(string name)
        {
            var w = new FrameWriter(Join);
            w.Byte(Version);
            w.String(CleanName(name));
            return w.ToArray();
        }

        public static byte[] BuildWelcome(RoomWelcome welcome)
        {
            var w = new FrameWriter(Welcome);
            w.Byte(Version);
            w.UInt16((ushort)welcome.MemberId);
            w.Byte((byte)Math.Clamp(welcome.MaxPlayers, 1, byte.MaxValue));
            w.String(welcome.GameId);
            w.String(CleanName(welcome.HostName));
            w.Byte((byte)Math.Clamp(welcome.ShareCount, 0, byte.MaxValue));
            return w.ToArray();
        }

        public static byte[] BuildReject(RoomRejectReason reason) => new[] { Reject, (byte)reason };

        public static byte[] BuildRoster(IReadOnlyCollection<RoomMember> members)
        {
            var w = new FrameWriter(Roster);
            w.Byte((byte)Math.Min(members.Count, byte.MaxValue));
            int written = 0;
            foreach (var m in members)
            {
                if (written++ == byte.MaxValue) break;
                w.UInt16((ushort)m.Id);
                w.Byte((byte)m.Status);
                w.UInt16(m.PingMs is int ping ? (ushort)Math.Clamp(ping, 0, NoPing - 1) : NoPing);
                w.String(m.Name);
            }
            return w.ToArray();
        }

        public static byte[] BuildChatSend(string text)
        {
            var w = new FrameWriter(ChatSend);
            w.Raw(Encoding.UTF8.GetBytes(CleanChat(text)));
            return w.ToArray();
        }

        public static byte[] BuildChat(RoomChatMessage message)
        {
            var w = new FrameWriter(Chat);
            w.UInt16((ushort)message.AuthorId);
            w.String(message.AuthorName);
            w.Raw(Encoding.UTF8.GetBytes(CleanChat(message.Text)));
            return w.ToArray();
        }

        public static byte[] BuildLeave() => new[] { Leave };

        public static byte[] BuildClosed() => new[] { Closed };

        public static byte[] BuildPing(uint number, long timestamp)
        {
            var frame = new byte[13];
            frame[0] = Ping;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), number);
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(5), timestamp);
            return frame;
        }

        /// <summary>Ответ на Ping: тот же кадр с другим типом.</summary>
        public static byte[] BuildPong(byte[] ping)
        {
            var frame = (byte[])ping.Clone();
            frame[0] = Pong;
            return frame;
        }

        // -- Разбор кадров. Повреждённый кадр — false, а не исключение: данные пришли из сети --

        public static bool TryParseJoin(byte[] frame, out byte version, out string name)
        {
            version = 0;
            name = "";
            var r = new FrameReader(frame);
            if (!r.Byte(out version) || !r.String(out var raw)) return false;
            name = CleanName(raw);
            return true;
        }

        public static bool TryParseWelcome(byte[] frame, out byte version, out RoomWelcome? welcome)
        {
            welcome = null;
            var r = new FrameReader(frame);
            if (!r.Byte(out version) || !r.UInt16(out var id) || !r.Byte(out var max) ||
                !r.String(out var gameId) || !r.String(out var hostName) || !r.Byte(out var shares))
            {
                return false;
            }
            welcome = new RoomWelcome(id, max, gameId, hostName, shares);
            return true;
        }

        public static bool TryParseReject(byte[] frame, out RoomRejectReason reason)
        {
            reason = default;
            if (frame.Length < 2) return false;
            reason = (RoomRejectReason)frame[1];
            return true;
        }

        public static bool TryParseRoster(byte[] frame, out List<RoomMember> members)
        {
            members = new List<RoomMember>();
            var r = new FrameReader(frame);
            if (!r.Byte(out var count)) return false;
            for (int i = 0; i < count; i++)
            {
                if (!r.UInt16(out var id) || !r.Byte(out var status) || !r.UInt16(out var ping) || !r.String(out var name)) return false;
                if (status > (byte)RoomMemberStatus.Left) return false;
                members.Add(new RoomMember(id, name, (RoomMemberStatus)status, ping == NoPing ? null : ping));
            }
            return true;
        }

        public static bool TryParseChatSend(byte[] frame, out string text)
        {
            text = CleanChat(Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
            return text.Length > 0;
        }

        public static bool TryParseChat(byte[] frame, out RoomChatMessage? message)
        {
            message = null;
            var r = new FrameReader(frame);
            if (!r.UInt16(out var authorId) || !r.String(out var author)) return false;
            var text = CleanChat(r.Rest());
            if (text.Length == 0) return false;
            message = new RoomChatMessage(authorId, author, text);
            return true;
        }

        public static bool TryParsePong(byte[] frame, out uint number, out long timestamp)
        {
            number = 0;
            timestamp = 0;
            if (frame.Length < 13 || frame[0] != Pong) return false;
            number = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1));
            timestamp = BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(5));
            return true;
        }

        // Строки — [длина в байтах (1)][UTF-8], не длиннее 255 байт
        private sealed class FrameWriter
        {
            private readonly List<byte> _bytes = new();

            public FrameWriter(byte type) => _bytes.Add(type);

            public void Byte(byte value) => _bytes.Add(value);

            public void UInt16(ushort value)
            {
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)value);
            }

            public void String(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                int length = bytes.Length;
                if (length > byte.MaxValue)
                {
                    // Режем по границе символа: не оставляем половину многобайтовой последовательности UTF-8
                    length = byte.MaxValue;
                    while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
                }
                _bytes.Add((byte)length);
                for (int i = 0; i < length; i++) _bytes.Add(bytes[i]);
            }

            public void Raw(byte[] bytes) => _bytes.AddRange(bytes);

            public byte[] ToArray() => _bytes.ToArray();
        }

        private ref struct FrameReader
        {
            private readonly byte[] _frame;
            private int _pos;

            public FrameReader(byte[] frame)
            {
                _frame = frame;
                _pos = 1; // Пропускаем тип кадра
            }

            public bool Byte(out byte value)
            {
                value = 0;
                if (_pos + 1 > _frame.Length) return false;
                value = _frame[_pos++];
                return true;
            }

            public bool UInt16(out ushort value)
            {
                value = 0;
                if (_pos + 2 > _frame.Length) return false;
                value = BinaryPrimitives.ReadUInt16BigEndian(_frame.AsSpan(_pos));
                _pos += 2;
                return true;
            }

            public bool String(out string value)
            {
                value = "";
                if (!Byte(out var length) || _pos + length > _frame.Length) return false;
                value = Encoding.UTF8.GetString(_frame, _pos, length);
                _pos += length;
                return true;
            }

            public string Rest() => Encoding.UTF8.GetString(_frame, _pos, _frame.Length - _pos);
        }
    }
}
