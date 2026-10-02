// MCTunnel.Tests/PacketTests.cs

using System;
using MC_Ref2207_NetSocketLib;
using Xunit;

namespace MCTunnel.Tests
{
    public class PacketTests
    {
        [Fact]
        public void RoundTrip_PreservesAllFields()
        {
            var original = new Packet(new byte[] { 1, 2, 3 }, seq: 42, ack: 7, type: PacketType.Ack);

            var restored = Packet.FromBytes(original.ToBytes());

            Assert.Equal(PacketType.Ack, restored.Type);
            Assert.Equal(42, restored.Sequence);
            Assert.Equal(7, restored.Acknowledgment);
            Assert.Equal(new byte[] { 1, 2, 3 }, restored.Payload);
        }

        // Длина нагрузки занимает 2 байта: значения больше 32767 не должны превращаться в отрицательные
        [Theory]
        [InlineData(0)]
        [InlineData(32767)]
        [InlineData(32768)]
        [InlineData(65000)]
        public void RoundTrip_PreservesPayloadOfAnyAllowedSize(int size)
        {
            var payload = new byte[size];
            new Random(size).NextBytes(payload);

            var restored = Packet.FromBytes(new Packet(payload, 1).ToBytes());

            Assert.Equal(payload, restored.Payload);
        }

        [Fact]
        public void Constructor_PayloadLargerThanUdpDatagram_Throws()
        {
            Assert.Throws<ArgumentException>(() => new Packet(new byte[65500], 1));
        }

        [Fact]
        public void FromBytes_TooShort_Throws()
        {
            Assert.Throws<ArgumentException>(() => Packet.FromBytes(new byte[5]));
        }
    }
}
