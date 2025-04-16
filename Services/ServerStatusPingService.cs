using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelegramBot_v2.Services
{
    public class ServerStatusPingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ServerStatusPingService> _logger;

        public ServerStatusPingService(HttpClient httpClient, ILogger<ServerStatusPingService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<string> PingAsync()
        {
            var url = "https://process.netsellerpay.com/ping.asp";
            _logger.LogInformation("Pinging server at {Url}", url);

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return "*Services Operational*";
                }
                else
                {
                    return "*Server offline.* Contact Cyberplumber immediately to resolve.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ping to server failed.");
                return "*Ping failed.* Please try again later.";
            }
        }
    }
}
