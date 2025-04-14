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

using JsonSerializer = System.Text.Json.JsonSerializer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});

builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();


app.UseHttpsRedirection();
app.MapControllers();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://*:{port}");

app.MapGet("/", () => "Telegram bot backend is running.");

var awaitingOrderId = new ConcurrentDictionary<long, string>();
var awaitingCompanyId = new ConcurrentDictionary<long, bool>();

string storageFilePath = Path.Combine("/home/site/wwwroot", "group_company_links.txt");

if (!File.Exists(storageFilePath))
{
    logger.LogInformation("Creating storage file at {FilePath}", storageFilePath);
    File.Create(storageFilePath).Close();
}

var groupCompanyMap = new ConcurrentDictionary<long, string>(
    File.ReadAllLines(storageFilePath)
        .Select(line => line.Split(','))
        .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
        .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
);

logger.LogInformation("Loaded {Count} group-company mappings from storage", groupCompanyMap.Count);

var botUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
             ?? throw new Exception("PUBLIC_URL environment variable is not set.");

var botClient = app.Services.GetRequiredService<TelegramBotClient>();
await botClient.SetWebhook(
    url: $"{botUrl}/api/bot",
    allowedUpdates: new[] {
        UpdateType.Message,
        UpdateType.CallbackQuery
    }
);
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
        logger.LogDebug("Raw JSON: {Json}", json);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        update = JsonSerializer.Deserialize<Update>(json, options);

        if (update == null)
        {
            logger.LogWarning("Update deserialized as null.");
            context.Response.StatusCode = 400;
            return;
        }

        logger.LogInformation("Update type: Message={HasMessage}, CallbackQuery={HasCallback}, EditedMessage={HasEditedMessage}, ChannelPost={HasChannelPost}",
            update.Message != null,
            update.CallbackQuery != null,
            update.EditedMessage != null,
            update.ChannelPost != null);

        if (update.CallbackQuery != null)
        {
            logger.LogInformation("Callback data: {CallbackData}", update.CallbackQuery.Data);

        }

        logger.LogDebug("Raw Update Dump: {Raw}", JsonSerializer.Serialize(update, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Deserialization failed");
        context.Response.StatusCode = 400;
        return;
    }

    if (update.CallbackQuery != null)
    {
        await HandleCallbackQuery(update.CallbackQuery, botClient);
    }
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

// === Callback Handler ===
async Task HandleCallbackQuery(CallbackQuery callback, TelegramBotClient botClient)
{
    var callbackChatId = callback.Message?.Chat.Id ?? 0;
    logger.LogInformation("Callback received: {CallbackData} from Chat ID: {ChatId}", callback.Data, callbackChatId);

    try
    {
        if (callback.Data == "check_status")
        {
            await HandlePaymentStatusRequest(callbackChatId, botClient);
        }
        else if (callback.Data == "help_info")
        {
            await HandleHelpRequest(callbackChatId, botClient);
        }
        else
        {
            logger.LogWarning("Unexpected callback data: {CallbackData}", callback.Data);
            await botClient.SendMessage(callbackChatId, "Unknown action. Please try again.");
        }

        await botClient.AnswerCallbackQuery(callback.Id);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in callback handler");
        if (callbackChatId != 0)
        {
            await botClient.SendMessage(callbackChatId, "Sorry, an error occurred processing your request.");
        }
    }
}

// === Message Handler ===
async Task HandleMessage(Message message, TelegramBotClient botClient)
{
    var chatId = message.Chat.Id;
    var text = message.Text?.Trim() ?? "";

    logger.LogInformation("Message received from Chat ID: {ChatId} - Text: {Text}", chatId, text);

    if (awaitingOrderId.TryGetValue(chatId, out var companyId))
    {
        awaitingOrderId.TryRemove(chatId, out _);
        var result = await QueryPaymentApiAsync(companyId, text);
        await botClient.SendMessage(chatId, result);
        return;
    }

    if (awaitingCompanyId.ContainsKey(chatId))
    {
        awaitingCompanyId.TryRemove(chatId, out _);

        if (!groupCompanyMap.ContainsKey(chatId))
        {
            groupCompanyMap[chatId] = text;
            File.AppendAllText(storageFilePath, $"{chatId},{text}{Environment.NewLine}");
            logger.LogInformation("Appended {ChatId},{Text} to file", chatId, text);
        }

        await botClient.SendMessage(chatId, $"Group registered with Company ID: {text}");
        return;
    }

    if (text.ToLower().Contains("/paymentstatus"))
    {
        await HandlePaymentStatusRequest(chatId, botClient);
    }
    else if (text.ToLower().Contains("/help"))
    {
        await HandleHelpRequest(chatId, botClient);
    }
    else if (text.Contains("@StatusPaymentBot"))
    {
        var keyboard = BuildKeyboardForUser(chatId);
        await botClient.SendMessage(chatId, "What would you like to do?", replyMarkup: keyboard);
    }
}

// === Dynamic Button Builder ===
InlineKeyboardMarkup BuildKeyboardForUser(long chatId)
{
    var buttons = new List<List<InlineKeyboardButton>>();

    if (groupCompanyMap.ContainsKey(chatId))
    {
        buttons.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("🧾 Payment Status", "check_status")
        });
    }

    buttons.Add(new List<InlineKeyboardButton>
    {
        InlineKeyboardButton.WithCallbackData("❓ Help", "help_info")
    });

    return new InlineKeyboardMarkup(buttons);
}

// === Payment Handler ===
async Task HandlePaymentStatusRequest(long chatId, TelegramBotClient botClient)
{
    if (groupCompanyMap.TryGetValue(chatId, out var registeredCompanyId))
    {
        awaitingOrderId[chatId] = registeredCompanyId;
        await botClient.SendMessage(chatId, "What's the Order ID?");
    }
    else
    {
        awaitingCompanyId[chatId] = true;
        await botClient.SendMessage(chatId, "Group not registered. Please reply with your Company ID to register.");
    }
}

// === Help Handler ===
async Task HandleHelpRequest(long chatId, TelegramBotClient botClient)
{
    await botClient.SendMessage(chatId,
        "*Help Guide*\n\n" +
        "• Use `/paymentstatus` to start a payment status request.\n" +
        "• Provide your Company ID and Order ID as prompted.\n" +
        "• You can mention me anytime with @StatusPaymentBot.",
        parseMode: ParseMode.Markdown);
}

// === API Call ===
async Task<string> QueryPaymentApiAsync(string companyId, string orderId)
{
    try
    {
        using var client = new HttpClient();
        var url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse>(json);

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
    catch (Exception e)
    {
        return "Failed to retrieve payment status: " + e.Message;
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
