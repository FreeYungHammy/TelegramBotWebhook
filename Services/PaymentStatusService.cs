using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
        var url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
        _logger.LogInformation("Querying API at {Url}", url);

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<ApiResponse>(content);

            if (apiResponse?.Data == null || apiResponse.Data.Count == 0)
                return $"Order {orderId}: No data found.";

            var data = apiResponse.Data[0];

            // Format transaction date
            string formattedDate = DateTime.TryParse(data.Trans_date, out var parsedDate)
                ? parsedDate.ToString("yyyy-MM-dd HH:mm:ss")
                : data.Trans_date;

            // Try to extract the GUID Transaction ID from ReplyDesc
            string extractedGuid = "";
            var guidMatch = System.Text.RegularExpressions.Regex.Match(data.ReplyDesc ?? "", @"transactionId:([a-f0-9\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (guidMatch.Success)
                extractedGuid = guidMatch.Groups[1].Value;

            // Split out internal details from reply description
            var simplifiedReply = data.ReplyDesc;

            string extraInfo = "";
            var respCodeMatch = System.Text.RegularExpressions.Regex.Match(simplifiedReply, @"ResponseCode[`]?\s*=\s*`?(\d+)`?");
            var respDescMatch = System.Text.RegularExpressions.Regex.Match(simplifiedReply, @"ResponseDescription[`]?\s*=\s*`?([^,\n]+)`?");
            var bankCodeMatch = System.Text.RegularExpressions.Regex.Match(simplifiedReply, @"BankCode[`]?\s*=\s*`?(\d+)`?");
            var bankDescMatch = System.Text.RegularExpressions.Regex.Match(simplifiedReply, @"BankDescription[`]?\s*=\s*`?([^\n`]+)`?");

            if (respCodeMatch.Success || respDescMatch.Success || bankCodeMatch.Success || bankDescMatch.Success)
            {
                extraInfo = $"Error: The transaction has failed with: ResponseCode = {respCodeMatch.Groups[1].Value}.\n" +
                            $"ResponseDescription = {respDescMatch.Groups[1].Value}\n" +
                            $"BankCode = {bankCodeMatch.Groups[1].Value}\n" +
                            $"BankDescription = {bankDescMatch.Groups[1].Value}";
            }

            return
                $"Order: {orderId}\n" +
                (string.IsNullOrEmpty(extractedGuid) ? "" : $"TransactionID: {extractedGuid}\n\n") +
                $"Date: {formattedDate}\n" +
                $"Transaction ID: {data.TransactionId}\n" +
                $"Response Code: {data.ReplyCode}\n" +
                (string.IsNullOrWhiteSpace(extraInfo) ? $"Response Description: {data.ReplyDesc}\n" : extraInfo + "\n") +
                $"Amount: {data.Amount} {data.Currency}\n" +
                $"Client: {data.Client_fullName}\n" +
                $"Email: {data.Client_email}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve payment status");
            return "Failed to retrieve payment status: " + ex.Message;
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
