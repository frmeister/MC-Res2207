// ConnectTogether.App/Services/RoomAddress.cs

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ConnectTogether.App.Models;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Адрес комнаты, который игрок получает от хоста: «203.0.113.5:47312», имя хоста «myhost.ddns.net:47312»
    /// или просто IP — тогда порт стандартный.
    /// </summary>
    public static class RoomAddress
    {
        public const int DefaultPort = AppSettings.DefaultAppPort;
        public const string Example = "203.0.113.5:47312";

        private static readonly Regex AddressInText = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?!\d)(?!\.\d)", RegexOptions.Compiled);
        private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$", RegexOptions.Compiled);

        /// <summary>
        /// Вставили сообщение от друга целиком — достаём из него адрес; иначе возвращаем текст без пробелов по краям.
        /// </summary>
        public static string Extract(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string trimmed = text.Trim();
            if (!trimmed.Any(char.IsWhiteSpace)) return trimmed;

            var match = AddressInText.Match(trimmed);
            return match.Success ? match.Value : trimmed;
        }

        /// <summary>Разбирает адрес; при ошибке error — понятное пользователю объяснение.</summary>
        public static bool TryParse(string? text, out string host, out int port, out string? error)
        {
            host = "";
            port = DefaultPort;
            error = null;
            string value = Extract(text);

            if (value.Length == 0)
            {
                error = $"Введите адрес, который прислал хост, например {Example}.";
                return false;
            }

            if (value.Count(c => c == ':') > 1 || value.StartsWith('['))
            {
                error = $"Адреса IPv6 пока не поддерживаются — нужен адрес вида {Example}.";
                return false;
            }

            int colon = value.IndexOf(':');
            host = colon >= 0 ? value[..colon] : value;
            if (colon >= 0 && (!int.TryParse(value[(colon + 1)..], out port) || port is < 1 or > 65535))
            {
                error = "Порт — число от 1 до 65535, например :47312.";
                return false;
            }

            bool looksLikeIp = host.All(c => char.IsDigit(c) || c == '.');
            if (looksLikeIp ? !IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || host.Count(c => c == '.') != 3
                            : !HostName.IsMatch(host))
            {
                error = $"Не похоже на адрес. Нужен вид IP:порт, например {Example}.";
                return false;
            }
            return true;
        }

        /// <summary>Адрес в виде для показа и сохранения: «host:port».</summary>
        public static string Format(string host, int port) => $"{host}:{port}";

        /// <summary>Находит IPv4-адрес хоста; null — имя не найдено.</summary>
        public static async Task<IPEndPoint?> ResolveAsync(string host, int port, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
                return addresses.Length > 0 ? new IPEndPoint(addresses[0], port) : null;
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }
}
