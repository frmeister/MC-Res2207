// MCTunnel.Core.PublicIp/PublicIpResolver.cs

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MCTunnel.Core.PublicIp
{
    public class PublicIpResolver
    {
        // Только IPv4-адреса сервисов: UdpPeer слушает IPv4, и IPv6-адрес второй стороне не поможет
        private static readonly string[] Urls = { "https://api4.ipify.org", "https://ipv4.icanhazip.com", "https://v4.ident.me" };
        private readonly HttpClient _httpClient;

        public PublicIpResolver(HttpClient? httpClient = null)
        {
            // Стандартный таймаут HttpClient — 100 с: при недоступном сервисе программа "висела" бы перед переходом к следующему
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public async Task<IPAddress?> GetPublicIpAddressAsync()
        {
            foreach (var url in Urls)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(url);
                    var trimmedResponse = response.Trim();
                    if (IPAddress.TryParse(trimmedResponse, out var ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ipAddress;
                    }
                    else
                    {
                        Trace.WriteLine($"[Core.PublicIp] Invalid IP format received from {url}: {trimmedResponse}");
                    }
                }
                catch (HttpRequestException hex)
                {
                    Trace.WriteLine($"[Core.PublicIp] HTTP error fetching IP from {url}: {hex.Message}");
                }
                catch (TaskCanceledException tcex)
                {
                    Trace.WriteLine($"[Core.PublicIp] Request timed out fetching IP from {url}: {tcex.Message}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Core.PublicIp] Exception thrown while fetching IP from {url}: {ex}");
                }
            }

            throw new Exception("Failed to retrieve public IP from any of the services.");
        }
    }
}