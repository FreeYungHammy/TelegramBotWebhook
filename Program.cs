using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using JsonSerializer = System.Text.Json.JsonSerializer; //to resolve ambiguous references 

var builder = WebApplication.CreateBuilder(args);

// Register required services
builder.Services.AddControllers();

// Add proper logging
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});

// Register TelegramBotClient
builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();

// Get logger
var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.UseHttpsRedirection();
app.MapControllers();

// Fix: Bind to Azure's required port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://*:{port}");

// Test route 
app.MapGet("/", () => "Telegram bot backend is running.");

// In-memory state
var awaitingOrderId = new ConcurrentDictionary<long, string>();
var awaitingCompanyId = new ConcurrentDictionary<long, bool>();

// Azure expected file path
string storageFilePath = Path.Combine("/home/site/wwwroot", "group_company_links.txt");

// Checking file exists
if (!File.Exists(storageFilePath))
{
    logger.LogInformation("Creating storage file at {FilePath}", storageFilePath);
    File.Create(storageFilePath).Close();
}

// Loading file data into memory
var groupCompanyMap = new ConcurrentDictionary<long, string>(
    File.ReadAllLines(storageFilePath)
        .Select(line => line.Split(','))
        .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
        .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
);

logger.LogInformation("Loaded {Count} group-company mappings from storage", groupCompanyMap.Count);

// === Set Webhook Automatically ===
var botUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
             ?? throw new Exception("PUBLIC_URL environment variable is not set.");

// Set the webhook URL (This happens automatically at startup)
var botClient = app.Services.GetRequiredService<TelegramBotClient>();
await botClient.SetWebhook($"{botUrl}/api/bot");
logger.LogInformation("Webhook set to {WebhookUrl}", $"{botUrl}/api/bot");

// === Webhook Entry Point ===
app.MapPost("/api/bot", async context =>
{
    logger.LogInformation("Incoming request to /api/bot");

    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        logger.LogDebug("Raw JSON: {Json}", json); // Log raw JSON to see if Telegram is sending data

        update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        if (update == null)
        {
            logger.LogWarning("Update deserialized as null.");
            context.Response.StatusCode = 400;
            return;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Deserialization failed");
        context.Response.StatusCode = 400;
        return;
    }

    // Handling callback query
    if (update.CallbackQuery != null)
    {
        await HandleCallbackQuery(update.CallbackQuery, botClient);
    }
    // Handling normal message input (like "/start" or "/paymentstatus")
    else if (update.Message != null && update.Message.Text != null)
    {
        await HandleMessage(update.Message, botClient);
    }
    else
    {
        logger.LogInformation("No message or callback query content.");
        return;
    }
});

// Handle callback query (button clicks)
async Task HandleCallbackQuery(CallbackQuery callback, TelegramBotClient botClient)
{
    var callbackChatId = callback.Message?.Chat.Id ?? 0;
    logger.LogInformation("Callback received: {CallbackData} from Chat ID: {ChatId}", callback.Data, callbackChatId);

    try
    {
        if (callback.Data == "check_status")
        {
            logger.LogInformation("Processing 'check_status'...");
            await HandlePaymentStatusRequest(callbackChatId, botClient);
        }
        else if (callback.Data == "help_info")
        {
            logger.LogInformation("Processing 'help_info'...");
            await HandleHelpRequest(callbackChatId, botClient);
        }

        // Important: Always acknowledge the callback
        await botClient.AnswerCallbackQuery(callback.Id);
        logger.LogInformation("Callback acknowledged.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in callback handler");
        // Try to send error message to user
        try
        {
            if (callbackChatId != 0)
            {
                await botClient.SendMessage(callbackChatId,
                    "Sorry, an error occurred processing your request.");
            }
        }
        catch (Exception innerEx)
        {
            logger.LogError(innerEx, "Error sending error message to user");
        }
    }
}

// Handle normal message input (like "/start" or "/paymentstatus")
async Task HandleMessage(Message message, TelegramBotClient botClient)
{
    var chatId = message.Chat.Id;
    var text = message.Text?.Trim() ?? string.Empty;

    logger.LogInformation("Message received at (UTC): {DateTime} from Chat ID: {ChatId}",
        message.Date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"), chatId);

    // Check if waiting for Order ID
    if (awaitingOrderId.TryGetValue(chatId, out var companyId))
    {
        logger.LogInformation("Received Order ID input: {OrderId} for Company ID: {CompanyId}", text, companyId);
        awaitingOrderId.TryRemove(chatId, out _);
        var result = await QueryPaymentApiAsync(companyId, text);
        await botClient.SendMessage(chatId, result);
        return;
    }

    // Check if waiting for Company ID
    if (awaitingCompanyId.TryGetValue(chatId, out _))
    {
        logger.LogInformation("Received Company ID registration: {CompanyId} for Chat ID: {ChatId}", text, chatId);
        awaitingCompanyId.TryRemove(chatId, out _);
        groupCompanyMap[chatId] = text;

        // Save to persistent storage
        File.WriteAllLines(storageFilePath,
            groupCompanyMap.Select(pair => $"{pair.Key},{pair.Value}"));

        logger.LogInformation("Updated storage file with new Company ID mapping");
        await botClient.SendMessage(chatId, $"Group registered with Company ID: {text}");
        return;
    }

    // Handle "/paymentstatus" command
    if (text.ToLower().Contains("/paymentstatus"))
    {
        await HandlePaymentStatusRequest(chatId, botClient);
    }
    // Handle "/help" command
    else if (text.ToLower().Contains("/help"))
    {
        await HandleHelpRequest(chatId, botClient);
    }
    else if (text.Contains("@StatusPaymentBot"))
    {
        // Send the inline keyboard on @ mention
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Payment Status", "check_status"),
                InlineKeyboardButton.WithCallbackData("Help", "help_info")
            }
        });

        await botClient.SendMessage(
            chatId,
            "What would you like to do?",
            replyMarkup: keyboard
        );
    }
}

// This function handles the "/paymentstatus" command logic
async Task HandlePaymentStatusRequest(long chatId, TelegramBotClient botClient)
{
    logger.LogInformation("Processing payment status request for Chat ID: {ChatId}", chatId);

    if (groupCompanyMap.TryGetValue(chatId, out var registeredCompanyId))
    {
        logger.LogInformation("Found registered Company ID: {CompanyId} for Chat ID: {ChatId}",
            registeredCompanyId, chatId);
        awaitingOrderId[chatId] = registeredCompanyId;
        await botClient.SendMessage(chatId, "What's the Order ID?");
    }
    else
    {
        logger.LogInformation("No registered Company ID found for Chat ID: {ChatId}", chatId);
        awaitingCompanyId[chatId] = true;
        await botClient.SendMessage(chatId, "Group not registered. Please reply with your Company ID to register.");
    }
}

// This function handles the "/help" command logic
async Task HandleHelpRequest(long chatId, TelegramBotClient botClient)
{
    logger.LogInformation("Processing help request for Chat ID: {ChatId}", chatId);

    await botClient.SendMessage(chatId,
        "*Help Guide*\n\n" +
        "• Use `/paymentstatus` to start a payment status request.\n" +
        "• Provide your Company ID and Order ID as prompted.\n" +
        "• You can mention me anytime with @StatusPaymentBot.",
        parseMode: ParseMode.Markdown);
}

// === API Helper ===
async Task<string> QueryPaymentApiAsync(string companyId, string orderId)
{
    logger.LogInformation("Querying payment API for Company ID: {CompanyId}, Order ID: {OrderId}",
        companyId, orderId);

    try
    {
        using var client = new HttpClient();
        var url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
        logger.LogInformation("API URL: {Url}", url);

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        logger.LogDebug("API returned raw: {Json}", json);

        var result = JsonSerializer.Deserialize<ApiResponse>(json);

        if (result?.Data == null || result.Data.Count == 0)
        {
            logger.LogWarning("No data found for Order ID: {OrderId}", orderId);
            return $"Order {orderId}: No data found.";
        }

        var data = result.Data[0];
        logger.LogInformation("Successfully retrieved payment status for Order ID: {OrderId}", orderId);

        return $"Order: {orderId}\n" +
               $"Date: {data.Trans_date}\n" +
               $"Transaction ID: {data.TransactionId}\n" +
               $"Response Code: {data.ReplyCode}\n" +
               $"Response Description: {data.ReplyDesc}\n" +
               $"Amount: {data.Amount} {data.Currency}\n" +
               $"Client: {data.Client_fullName}\n" +
               $"Email: {data.Client_email}";
    }
    catch (Exception e)
    {
        logger.LogError(e, "API error for Company ID: {CompanyId}, Order ID: {OrderId}",
            companyId, orderId);
        return "Failed to retrieve payment status.";
    }
}

app.Run();

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