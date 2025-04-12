using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using JsonSerializer = System.Text.Json.JsonSerializer; //to resolve ambiguous references 

var builder = WebApplication.CreateBuilder(args);

// Register required services
builder.Services.AddControllers();

// Register TelegramBotClient
builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();

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
    File.Create(storageFilePath).Close();
}

// Loading file data into memory
var groupCompanyMap = new ConcurrentDictionary<long, string>(
    File.ReadAllLines(storageFilePath)
        .Select(line => line.Split(','))
        .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
        .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
);

// === Set Webhook Automatically ===
var botUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
             ?? throw new Exception("PUBLIC_URL environment variable is not set.");

// Set the webhook URL (This happens automatically at startup)
var botClient = app.Services.GetRequiredService<TelegramBotClient>();
await botClient.SetWebhook($"{botUrl}/api/bot");

// === Webhook Entry Point ===
app.MapPost("/api/bot", async context =>
{
    Console.WriteLine("Incoming request to /api/bot");

    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        Console.WriteLine("Raw JSON: " + json); // Log raw JSON to see if Telegram is sending data

        update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        if (update == null)
        {
            Console.WriteLine("Update deserialized as null.");
            context.Response.StatusCode = 400;
            return;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Deserialization failed: " + ex.Message);
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
        Console.WriteLine("No message or callback query content.");
        return;
    }
});

// Handle callback query (button clicks)
async Task HandleCallbackQuery(CallbackQuery callback, TelegramBotClient botClient)
{
    var callbackChatId = callback.Message.Chat.Id;
    Console.WriteLine($"Callback received: {callback.Data}");

    try
    {
        if (callback.Data == "check_status")
        {
            Console.WriteLine("Processing 'check_status'...");
            await HandlePaymentStatusRequest(callbackChatId, botClient);
        }
        else if (callback.Data == "help_info")
        {
            Console.WriteLine("Processing 'help_info'...");
            await HandleHelpRequest(callbackChatId, botClient);
        }

        await botClient.AnswerCallbackQuery(callback.Id);
        Console.WriteLine("Callback acknowledged.");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Error in callback handler: " + ex.Message);
    }
}

// Handle normal message input (like "/start" or "/paymentstatus")
async Task HandleMessage(Message message, TelegramBotClient botClient)
{
    var chatId = message.Chat.Id;
    var text = message.Text.Trim();

    Console.WriteLine($"Message received at (UTC): {message.Date.ToUniversalTime():yyyy-MM-dd HH:mm:ss} from Chat ID: {chatId}");

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

// This function simulates the "/paymentstatus" command logic
async Task HandlePaymentStatusRequest(long chatId, TelegramBotClient botClient)
{
    Console.WriteLine("Simulating '/paymentstatus'...");

    if (groupCompanyMap.TryGetValue(chatId, out var registeredCompanyId))
    {
        awaitingOrderId[chatId] = registeredCompanyId;
        await botClient.SendMessage(chatId, "What’s the Order ID?");
    }
    else
    {
        awaitingCompanyId[chatId] = true;
        await botClient.SendMessage(chatId, "Group not registered. Please reply with your Company ID to register.");
    }
}

// This function simulates the "/help" command logic
async Task HandleHelpRequest(long chatId, TelegramBotClient botClient)
{
    Console.WriteLine("Simulating '/help'...");

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
    try
    {
        using var client = new HttpClient();
        var url = $"https://process.highisk.com/member/getstatusBOT.asp?CompanyNum={companyId}&Order={orderId}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Console.WriteLine("API returned raw: " + json);

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
        Console.WriteLine("API error: " + e.Message);
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
