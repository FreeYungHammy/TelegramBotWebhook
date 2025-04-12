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
using System.Text.RegularExpressions;
using System.Threading;

using JsonSerializer = System.Text.Json.JsonSerializer; // Fix ambiguous reference

var builder = WebApplication.CreateBuilder(args);

// Register required services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register TelegramBotClient
builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Bind to Azure's required port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://*:{port}");

// Optional test route
app.MapGet("/", () => "Telegram bot backend is running.");

// Setup in-memory state
var awaitingOrderId = new ConcurrentDictionary<long, string>();
var awaitingCompanyId = new ConcurrentDictionary<long, bool>();

// Azure expected file path
string storageFilePath = Path.Combine("/home/site/wwwroot", "group_company_links.txt");

// Ensure file exists
if (!File.Exists(storageFilePath))
{
    File.Create(storageFilePath).Close();
}

// Load file data into memory
var groupCompanyMap = new ConcurrentDictionary<long, string>(
    File.ReadAllLines(storageFilePath)
        .Select(line => line.Split(','))
        .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
        .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
);

// === Webhook Entry Point ===
app.MapPost("/bot", async context =>
{
    Console.WriteLine("Incoming request to /bot");

    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        Console.WriteLine("Raw JSON: " + json);

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

    if (update.CallbackQuery != null)
    {
        Console.WriteLine("CallbackQuery branch hit");
        var callback = update.CallbackQuery;
        var callbackChatId = callback.Message.Chat.Id;
        Console.WriteLine($"Callback received: {callback.Data}");

        try
        {
            if (callback.Data == "check_status")
            {
                Console.WriteLine("Processing 'check_status'...");

                if (groupCompanyMap.TryGetValue(callbackChatId, out var companyId))
                {
                    awaitingOrderId[callbackChatId] = companyId;
                    await botClient.SendMessage(callbackChatId, "What’s the Order ID?", cancellationToken: CancellationToken.None);
                }
                else
                {
                    awaitingCompanyId[callbackChatId] = true;
                    await botClient.SendMessage(callbackChatId, "Group not registered. Please reply with your Company ID to register.", cancellationToken: CancellationToken.None);
                }
            }
            else if (callback.Data == "help_info")
            {
                Console.WriteLine("Processing 'help_info'...");

                await botClient.SendMessage(callbackChatId,
                    "*Help Guide*\n\n" +
                    "• Use `/paymentstatus` to start a payment status request.\n" +
                    "• Provide your Company ID and Order ID as prompted.\n" +
                    "• You can mention me anytime with @StatusPaymentBot.",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: CancellationToken.None);
            }

            await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: CancellationToken.None);
            Console.WriteLine("Callback acknowledged.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error in callback handler: " + ex.Message);
        }

        return;
    }
    else if (update.Message != null && update.Message.Text != null)
    {
        Console.WriteLine("Message branch hit");
        var chat = update.Message.Chat;
        var text = update.Message.Text.Trim();
        var chatId = chat.Id;
        var date = update.Message.Date;
        Console.WriteLine($"Message received at (UTC): {date.ToUniversalTime():yyyy-MM-dd HH:mm:ss} from Chat ID: {chatId}");

        if (text.ToLower().Contains("/paymentstatus"))
        {
            if (groupCompanyMap.TryGetValue(chatId, out var registeredCompanyId))
            {
                awaitingOrderId[chatId] = registeredCompanyId;
                await botClient.SendMessage(chatId, "What’s the Order ID?", cancellationToken: CancellationToken.None);
            }
            else
            {
                awaitingCompanyId[chatId] = true;
                await botClient.SendMessage(chatId, "Group not registered. Please reply with your Company ID to register.", cancellationToken: CancellationToken.None);
            }
            return;
        }

        if (awaitingCompanyId.ContainsKey(chatId))
        {
            var companyId = text.Trim();
            groupCompanyMap[chatId] = companyId;
            awaitingCompanyId.TryRemove(chatId, out _);
            awaitingOrderId[chatId] = companyId;

            await File.AppendAllTextAsync(storageFilePath, $"{chatId},{companyId}{Environment.NewLine}");

            await botClient.SendMessage(chatId, $"Company ID '{companyId}' registered successfully!", cancellationToken: CancellationToken.None);
            await botClient.SendMessage(chatId, "What’s the Order ID?", cancellationToken: CancellationToken.None);
            return;
        }

        if (awaitingOrderId.TryRemove(chatId, out var companyIdToUse))
        {
            var result = await QueryPaymentApiAsync(companyIdToUse, text);
            await botClient.SendMessage(chatId, result, cancellationToken: CancellationToken.None);
            return;
        }

        if (text.ToLower().Contains("/start") || text.Contains("@StatusPaymentBot"))
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Payment Status", "check_status"),
                    InlineKeyboardButton.WithCallbackData("Help", "help_info")
                }
            });

            await botClient.SendMessage(chatId, "What would you like to do?", replyMarkup: keyboard, cancellationToken: CancellationToken.None);
            return;
        }
    }
    else
    {
        Console.WriteLine("Neither message nor callback handled");
        return;
    }
});

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
        var result = JsonSerializer.Deserialize<ApiResponse>(json);

        if (result?.Data == null || result.Data.Count == 0)
        {
            return $"Order {orderId}: No data found.";
        }

        var data = result.Data[0];

        if (data.ReplyDesc.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            string GetField(string label)
            {
                var match = Regex.Match(data.ReplyDesc, $"`{label}`\\s*=\\s*`([^`]*)`");
                return match.Success ? match.Groups[1].Value.Trim().Split("]},")[0] : "";
            }

            var responseCode = GetField("ResponseCode");
            var responseDesc = GetField("ResponseDescription");
            var bankCode = GetField("BankCode");
            var bankDesc = GetField("BankDescription");

            var transactionId = "";
            if (data.ReplyDesc.Contains("transactionId:"))
            {
                var parts = data.ReplyDesc.Split("transactionId:");
                if (parts.Length > 1)
                    transactionId = parts[1].Trim().Trim('`', '}', ']', ',', ' ');
            }

            return $"Order: {orderId}\n" +
                   $"Date: {data.Trans_date}\n" +
                   $"Transaction ID: {transactionId}\n" +
                   $"Response Code: {responseCode}\n" +
                   $"Response Description: {responseDesc}\n" +
                   $"Bank Code: {bankCode}\n" +
                   $"Bank Description: {bankDesc}\n" +
                   $"Client: {data.Client_fullName}\n" +
                   $"Email: {data.Client_email}";
        }
        else
        {
            return $"Order: {orderId}\n" +
                   $"Date: {data.Trans_date}\n" +
                   $"Status: {data.ReplyDesc}\n" +
                   $"Client: {data.Client_fullName}\n" +
                   $"Email: {data.Client_email}";
        }
    }
    catch (Exception e)
    {
        Console.WriteLine("API error: " + e.Message);
        return "Failed to retrieve payment status.";
    }
}

public class ApiResponse
{
    public List<ApiData> Data { get; set; } = new();
}

public class ApiData
{
    public string ReplyDesc { get; set; } = "";
    public string Trans_date { get; set; } = "";
    public string Client_fullName { get; set; } = "";
    public string Client_email { get; set; } = "";
}
