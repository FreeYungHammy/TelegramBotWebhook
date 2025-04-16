using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelegramBot_v2.Services
{
    public class BlacklistService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<BlacklistService> _logger;

        public BlacklistService(HttpClient httpClient, ILogger<BlacklistService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<string> BlacklistPhoneNumberAsync(string phoneNumber)
        {
            var url = "https://your-api/blacklist/phone"; 
            var content = new StringContent(JsonSerializer.Serialize(new { phoneNumber }), Encoding.UTF8, "application/json");

            return await SendRequestAsync(url, content);
        }

        public async Task<string> BlacklistEmailAsync(string email)
        {
            var url = "https://your-api/blacklist/email"; 
            var content = new StringContent(JsonSerializer.Serialize(new { email }), Encoding.UTF8, "application/json");

            return await SendRequestAsync(url, content);
        }

        public async Task<string> BlacklistCardFirst6Async(string first6Digits)
        {
            var url = "https://your-api/blacklist/card/first6"; 
            var content = new StringContent(JsonSerializer.Serialize(new { first6Digits }), Encoding.UTF8, "application/json");

            return await SendRequestAsync(url, content);
        }

        public async Task<string> BlacklistCardLast4Async(string last4Digits)
        {
            var url = "https://your-api/blacklist/card/last4"; 
            var content = new StringContent(JsonSerializer.Serialize(new { last4Digits }), Encoding.UTF8, "application/json");

            return await SendRequestAsync(url, content);
        }

        private async Task<string> SendRequestAsync(string url, HttpContent content)
        {
            _logger.LogInformation("Sending blacklist request to {Url}", url);

            try
            {
                var response = await _httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    return "User successfully blacklisted.";
                }
                else
                {
                    return $"Failed to blacklist. Status: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Blacklist request failed.");
                return "Blacklist service failed. Please try again later.";
            }
        }
    }
}
