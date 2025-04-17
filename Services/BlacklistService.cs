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

        private readonly string _endpointUrl = "https://process.netsellerpay.com/";

        public BlacklistService(HttpClient httpClient, ILogger<BlacklistService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<string> SubmitBlacklistAsync(string filterValue, string filterType, string comments = "Blacklisted via TelegramBot")
        {
            var content = new FormUrlEncodedContent(new[]
            {
        new KeyValuePair<string, string>("filterValue", filterValue),
        new KeyValuePair<string, string>("filterType", filterType),
        new KeyValuePair<string, string>("comments", comments)
    });

            try
            {
                _logger.LogInformation("Submitting blacklist to {Url} with {Type}: {Value}", _endpointUrl, filterType, filterValue);

                var response = await _httpClient.PostAsync(_endpointUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Blacklist submitted successfully. Response: {Response}", responseContent);
                    return "User successfully blacklisted.";
                }
                else
                {
                    _logger.LogWarning("Blacklist submission failed. Response: {Response}", responseContent);
                    return $"Failed to submit blacklist. Server response: {responseContent}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during blacklist submission.");
                return $"Error while submitting blacklist: {ex.Message}";
            }
        }

    }
}
