// MCTunnel.Core.PublicIp/PublicIpResolver.cs

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MCTunnel.Core.PublicIp
{
    public class PublicIpResolver
    {
        private static readonly string[] Urls = { "https://api.ipify.org", "https://icanhazip.com", "https://ident.me" };
        private readonly HttpClient _httpClient;

        public PublicIpResolver(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<IPAddress?> GetPublicIpAddressAsync()
        {
            foreach (var url in Urls)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(url);
                    var trimmedResponse = response.Trim();
                    if (IPAddress.TryParse(trimmedResponse, out var ipAddress))
                    {
                        return ipAddress;
                    }
                    else
                    {
                        Debug.WriteLine($"[Core.PublicIp] Invalid IP format received from {url}: {trimmedResponse}");
                    }
                }
                catch (HttpRequestException hex)
                {
                    Debug.WriteLine($"[Core.PublicIp] HTTP error fetching IP from {url}: {hex.Message}");
                }
                catch (TaskCanceledException tcex)
                {
                    Debug.WriteLine($"[Core.PublicIp] Request timed out fetching IP from {url}: {tcex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Core.PublicIp] Exception thrown while fetching IP from {url}: {ex}");
                }
            }

            throw new Exception("Failed to retrieve public IP from any of the services.");
        }
    }
}