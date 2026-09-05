using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MC_Ref2207_NetSocketLib
{
    public enum PacketType : byte
    {
        Data = 0,
        Ack = 1,
        Hello = 2
    }
    public class Packet
    {

        // Архитектуру придумал ИИ, так что я мог неправильно понять идею этого класса
        // Скорее всего он нужен для формирования пакета, как его сборщик и декомпилятор пакета в одном лице
        // Структура пакета:
        // 0 байт - data = 0, ack = 1
        // 1-4: Sequence = int, big-endian
        // 5-8: Acknowledgement = int, big-endian
        // 9-10: Payload length = short, big-endian
        // 11... Payload

        public PacketType Type { get; }
        public int Sequence { get; }
        public int Acknowledgment { get; }
        public byte[] Payload { get; }
        // Sequence

        // Конструктор
        public Packet(byte[] payload, int seq, int ack = 0, PacketType type = PacketType.Data)
        {

            // Проверяем получили ли мы пустую нагрузку на ввод
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            // Проверка размера нагрузки на превышение допустимой
            if (payload.Length > ushort.MaxValue) throw new ArgumentException("Payload too large!", nameof(payload));

            Payload = payload;
            Acknowledgment = ack;
            Type = type;
            Sequence = seq;
        }

        // Сериализация в байты
        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((byte)Type);
                writer.Write(IPAddress.HostToNetworkOrder(Sequence));
                writer.Write(IPAddress.HostToNetworkOrder(Acknowledgment));
                writer.Write((short)IPAddress.HostToNetworkOrder(Payload.Length));
                writer.Write(Payload);
                return ms.ToArray();
            }
        }

        // Десериализация из байтов
        public static Packet FromBytes(byte[]? data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (data.Length < 11) throw new ArgumentException("Data too short for valid packet", nameof(data));

            using (var ms = new MemoryStream(data))
            using(var reader = new BinaryReader(ms))
            {
                var type = (PacketType)reader.ReadByte();
                int seq = IPAddress.NetworkToHostOrder(reader.ReadInt32());
                int ack = IPAddress.NetworkToHostOrder(reader.ReadInt32());
                short len = IPAddress.NetworkToHostOrder(reader.ReadInt16());
                if (len < 0 || len > data.Length - 11) throw new ArgumentException("Invalid payload length");

                byte[] payload = reader.ReadBytes(len);
                return new Packet(payload, seq, ack, type);
            }
 
        }

        // Дополнительно
        public override string ToString()
        {
            return $"Packet(Type={Type}, Seq={Sequence}), Ack={Acknowledgment}, PayloadLen={Payload.Length}, Payload={Payload}";
        }

        // Константы размерности заголовка
        public static class PacketHeaderSizes
        {
            public const int Type = 1;
            public const int Sequence = 4;
            public const int Ack = 4;
            public const int Length = 2;
            public const int Total = Type + Sequence + Ack + Length; // 11
        }
    }
}
