// ConnectTogether.App.Tests/TextAndAddressTests.cs

using System.Text.RegularExpressions;
using ConnectTogether.App.Services;
using ConnectTogether.App.ViewModels;

namespace ConnectTogether.App.Tests
{
    public class RoomAddressTests
    {
        [Theory]
        [InlineData("203.0.113.5:47312", "203.0.113.5", 47312)]
        [InlineData(" 203.0.113.5:3858 ", "203.0.113.5", 3858)]
        [InlineData("203.0.113.5", "203.0.113.5", 47312)]
        [InlineData("myhost.ddns.net:50000", "myhost.ddns.net", 50000)]
        [InlineData("localhost", "localhost", 47312)]
        public void TryParse_AcceptsIpOrNameWithOptionalPort(string input, string host, int port)
        {
            Assert.True(RoomAddress.TryParse(input, out var parsedHost, out var parsedPort, out var error), error);
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
        }

        [Theory]
        [InlineData("")]
        [InlineData("203.0.113.5:0")]
        [InlineData("203.0.113.5:70000")]
        [InlineData("203.0.113:47312")]
        [InlineData("300.0.113.5:47312")]
        [InlineData("[2001:db8::1]:47312")]
        [InlineData("2001:db8::1")]
        [InlineData("my host:47312")]
        public void TryParse_RejectsWithExplanation(string input)
        {
            Assert.False(RoomAddress.TryParse(input, out _, out _, out var error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        // Друг может вставить сообщение целиком — адрес извлекается из него
        [Fact]
        public void Extract_FindsAddressInInviteMessage()
        {
            string message = "Привет! Заходи ко мне в Rust через ConnectTogether. Адрес комнаты: 203.0.113.5:47312. Приложение: connecttogether.app";
            Assert.Equal("203.0.113.5:47312", RoomAddress.Extract(message));
            Assert.Equal("203.0.113.5:47312", RoomAddress.Extract("  203.0.113.5:47312  "));
        }
    }

    public class RussianTextTests
    {
        [Theory]
        [InlineData(1, "игрок")]
        [InlineData(2, "игрока")]
        [InlineData(4, "игрока")]
        [InlineData(5, "игроков")]
        [InlineData(11, "игроков")]
        [InlineData(14, "игроков")]
        [InlineData(21, "игрок")]
        [InlineData(22, "игрока")]
        [InlineData(111, "игроков")]
        public void Plural_PicksRussianForm(int n, string expected)
        {
            Assert.Equal(expected, RussianText.Plural(n, "игрок", "игрока", "игроков"));
        }

        [Theory]
        [InlineData("Лёша", "Лёши")]
        [InlineData("Марина", "Марины")]
        [InlineData("Тёма", "Тёмы")]
        [InlineData("Юля", "Юли")]
        [InlineData("Денис", "Дениса")]
        [InlineData("Андрей", "Андрея")]
        [InlineData("Игорь", "Игоря")]
        [InlineData("kostya_2008", "kostya_2008")]
        [InlineData("Я", "Я")]
        public void Genitive_DeclinesCommonNamesAndKeepsNicknames(string name, string expected)
        {
            Assert.Equal(expected, RussianText.Genitive(name));
        }

        [Fact]
        public void RelativeTime_DescribesRecentDays()
        {
            var now = new DateTime(2026, 10, 3, 12, 0, 0);
            Assert.Equal("сегодня, 09:05", RussianText.RelativeTime(new DateTime(2026, 10, 3, 9, 5, 0), now));
            Assert.Equal("вчера, 22:10", RussianText.RelativeTime(new DateTime(2026, 10, 2, 22, 10, 0), now));
            Assert.Equal("2 дня назад", RussianText.RelativeTime(new DateTime(2026, 10, 1, 8, 0, 0), now));
            Assert.Equal("6 дней назад", RussianText.RelativeTime(new DateTime(2026, 9, 27, 8, 0, 0), now));
            Assert.Equal("21 сентября", RussianText.RelativeTime(new DateTime(2026, 9, 21, 8, 0, 0), now));
            Assert.Equal("28 декабря 2025", RussianText.RelativeTime(new DateTime(2025, 12, 28, 8, 0, 0), now));
        }

        [Fact]
        public void Duration_ShowsHoursAndMinutes()
        {
            Assert.Equal("меньше минуты", RussianText.Duration(TimeSpan.FromSeconds(40)));
            Assert.Equal("5 мин", RussianText.Duration(TimeSpan.FromMinutes(5)));
            Assert.Equal("1 ч 12 мин", RussianText.Duration(TimeSpan.FromMinutes(72)));
        }
    }

    public class PingTests
    {
        // Пороги из стайлгайда: до 60 мс — отлично, 60–120 — нормально, больше 120 — плохо
        [Theory]
        [InlineData(null, PingQuality.None)]
        [InlineData(34, PingQuality.Good)]
        [InlineData(59, PingQuality.Good)]
        [InlineData(60, PingQuality.Fair)]
        [InlineData(120, PingQuality.Fair)]
        [InlineData(121, PingQuality.Poor)]
        public void Quality_FollowsStyleguideThresholds(int? ms, PingQuality expected)
        {
            Assert.Equal(expected, Ping.Quality(ms));
        }
    }
}
