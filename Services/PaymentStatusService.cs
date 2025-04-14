using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TelegramBot_v2.Services
{
    public class PaymentStatusService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PaymentStatusService> _logger;

        public PaymentStatusService(HttpClient httpClient, ILogger<PaymentStatusService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<string> QueryPaymentStatusAsync(string companyId, string orderId)
        {
            try
            {
                string url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
                _logger.LogInformation("Querying API at {Url}", url);

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                });

                if (result?.Data == null || result.Data.Count == 0)
                {
                    return $"Order {orderId}: No data found.";
                }

                var data = result.Data[0];

                return $"Order: {orderId}\n" +
                       $"Date: {data.Trans_date}\n" +
                       $"Transaction ID: {data.TransactionId}\n" +
                       $"Response Code: {data.ReplyCode}\n" +
                       $"Response Description: {data.ReplyDesc}\n" +
                       $"Amount: {data.Amount} {data.Currency}\n" +
                       $"Client: {data.Client_fullName}\n" +
                       $"Email: {data.Client_email}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while querying payment API");
                return "❌ Failed to retrieve payment status.";
            }
        }
    }

    public class ApiResponse
    {
        [JsonPropertyName("data")]
        public List<ApiData> Data { get; set; } = new();
    }

    public class ApiData
    {
        [JsonPropertyName("replyDesc")]
        public string ReplyDesc { get; set; } = "";

        [JsonPropertyName("trans_date")]
        public string Trans_date { get; set; } = "";

        [JsonPropertyName("client_fullName")]
        public string Client_fullName { get; set; } = "";

        [JsonPropertyName("client_email")]
        public string Client_email { get; set; } = "";

        [JsonPropertyName("trans_id")]
        public string TransactionId { get; set; } = "";

        [JsonPropertyName("replyCode")]
        public string ReplyCode { get; set; } = "";

        [JsonPropertyName("merchantID")]
        public string MerchantId { get; set; } = "";

        [JsonPropertyName("trans_amount")]
        public string Amount { get; set; } = "";

        [JsonPropertyName("trans_currency")]
        public string Currency { get; set; } = "";
    }
}
