using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

public class PaymentStatusService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PaymentStatusService> _logger;

    public PaymentStatusService(HttpClient httpClient, ILogger<PaymentStatusService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<(bool found, string response)> QueryPaymentStatusAsync(string companyId, string orderId)
    {
        var url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
        _logger.LogInformation("Querying API at {Url}", url);

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<ApiResponse>(content);

            if (apiResponse?.Data == null || apiResponse.Data.Count == 0)
                return (false, $"No data found for Order#: {orderId}");

            var data = apiResponse.Data[0];

            // Format transaction date
            string formattedDate = DateTime.TryParse(data.Trans_date, out var parsedDate)
                ? parsedDate.ToString("yyyy-MM-dd HH:mm:ss")
                : data.Trans_date;

            // Extract embedded GUID Transaction ID
            string extractedGuid = Regex.Match(data.ReplyDesc ?? "", @"transactionId:([a-f0-9\-]+)", RegexOptions.IgnoreCase).Groups[1].Value;

            // Extract clean reply data
            string extraInfo = "";
            var respCode = Regex.Match(data.ReplyDesc, @"ResponseCode[`]?\s*=\s*`?(\d+)`?");
            var respDesc = Regex.Match(data.ReplyDesc, @"ResponseDescription[`]?\s*=\s*`?([^,\]\n\r]+)`?");
            var bankCode = Regex.Match(data.ReplyDesc, @"BankCode[`]?\s*=\s*`?(\d+)`?");
            var bankDesc = Regex.Match(data.ReplyDesc, @"BankDescription[`]?\s*=\s*`?([^\]\},\n\r]+)`?");

            if (!string.IsNullOrWhiteSpace(respCode.Value) || !string.IsNullOrWhiteSpace(respDesc.Value) ||
                !string.IsNullOrWhiteSpace(bankCode.Value) || !string.IsNullOrWhiteSpace(bankDesc.Value))
            {
                extraInfo = "Error: The transaction has failed with: " +
                            (respCode.Success ? $"ResponseCode = {respCode.Groups[1].Value}.\n" : "") +
                            (respDesc.Success ? $"ResponseDescription = {respDesc.Groups[1].Value}\n" : "") +
                            (bankCode.Success ? $"BankCode = {bankCode.Groups[1].Value}\n" : "") +
                            (bankDesc.Success ? $"BankDescription = {bankDesc.Groups[1].Value}\n" : "");
            }

            string formattedResponse =
                $"Order#: {orderId}\n" +
                (!string.IsNullOrEmpty(extractedGuid) ? $"TransactionID: {extractedGuid}\n\n" : "") +
                $"Date: {formattedDate}\n\n" +
                $"Response Code: {data.ReplyCode}\n" +
                (!string.IsNullOrWhiteSpace(extraInfo) ? extraInfo : $"Response Description: {data.ReplyDesc}\n") +
                $"Amount: {data.Amount} {data.Currency}\n" +
                $"Client: {data.Client_fullName}\n" +
                $"Email: {data.Client_email}";

            return (true, formattedResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve transaction status");
            return (false, "Failed to retrieve transaction status: " + ex.Message);
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
