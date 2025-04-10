using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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
    Console.WriteLine("\ud83d\udce9 Incoming request to /bot");

    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        Console.WriteLine("\u2699\ufe0f Raw JSON: " + json);
        var settings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new SafeEnumConverter<MessageEntityType>() }
        };
        update = JsonConvert.DeserializeObject<Update>(json, settings);
    }
    catch (Exception ex)
    {
        Console.WriteLine("\u274c Deserialization failed: " + ex.Message);
        context.Response.StatusCode = 400;
        return;
    }

    if (update?.Message == null || update.Message.Text == null)
        return;

    var chat = update.Message.Chat;
    var text = update.Message.Text.Trim();
    var chatId = chat.Id;

    // Register payment intent
    if (text.ToLower().Contains("/paymentstatus"))
    {
        if (groupCompanyMap.TryGetValue(chatId, out var registeredCompanyId))
        {
            awaitingOrderId[chatId] = registeredCompanyId;
            await botClient.SendTextMessageAsync(chatId, "What’s the Order ID?");
        }
        else
        {
            awaitingCompanyId[chatId] = true;
            await botClient.SendTextMessageAsync(chatId, "Group not registered. Please reply with your Company ID to register.");
        }
        return;
    }

    // Register company ID if awaiting
    if (awaitingCompanyId.ContainsKey(chatId))
    {
        var companyId = text.Trim();
        groupCompanyMap[chatId] = companyId;
        awaitingCompanyId.TryRemove(chatId, out _);
        awaitingOrderId[chatId] = companyId;

        await File.AppendAllTextAsync(storageFilePath, $"{chatId},{companyId}{Environment.NewLine}");

        await botClient.SendTextMessageAsync(chatId, $"Company ID '{companyId}' registered successfully!");
        await botClient.SendTextMessageAsync(chatId, "What’s the Order ID?");
        return;
    }

    // Handle order ID
    if (awaitingOrderId.TryRemove(chatId, out var companyIdToUse))
    {
        var result = await QueryPaymentApiAsync(companyIdToUse, text);
        await botClient.SendTextMessageAsync(chatId, result);
        return;
    }

    // Handle bot mention
    if (text.Contains("@StatusPaymentBot"))
    {
        if (groupCompanyMap.TryGetValue(chatId, out var cid))
        {
            awaitingOrderId[chatId] = cid;
            await botClient.SendTextMessageAsync(chatId, "What’s the Order ID?");
        }
        else
        {
            awaitingCompanyId[chatId] = true;
            await botClient.SendTextMessageAsync(chatId, "Group not registered. Please reply with your Company ID to register.");
        }
    }
});

// === Webhook Setter ===
app.MapGet("/setwebhook", async context =>
{
    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    var domain = Environment.GetEnvironmentVariable("PUBLIC_URL")
        ?? throw new Exception("PUBLIC_URL not set.");
    var webhookUrl = $"{domain}/bot";

    await botClient.SetWebhookAsync(webhookUrl);
    await context.Response.WriteAsync("Webhook set!");
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

// === API Helper ===
async Task<string> QueryPaymentApiAsync(string companyId, string orderId)
{
    try
    {
        using var client = new HttpClient();
        var url = $"https://process.highisk.com/member/getstatus.asp?CompanyNum={companyId}&Order={orderId}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<ApiResponse>(json);

        if (result?.Data == null || result.Data.Count == 0)
        {
            return $"❌ Order {orderId}: No data found.";
        }

        var data = result.Data[0];
        return $"Order: {orderId}\nDate: {data.Trans_date}\nStatus: {data.ReplyDesc}\nClient: {data.Client_fullName}\nEmail: {data.Client_email}";
    }
    catch (Exception e)
    {
        Console.WriteLine("API error: " + e.Message);
        return "Failed to retrieve payment status.";
    }
}

// === API Models ===
public class ApiResponse
{
    public string Error { get; set; } = "";
    public string Message { get; set; } = "";
    public List<ApiData> Data { get; set; } = new();
}

public class ApiData
{
    public string ReplyDesc { get; set; } = "";
    public string Trans_date { get; set; } = "";
    public string Client_fullName { get; set; } = "";
    public string Client_email { get; set; } = "";
}

// === Safe Enum Converter ===
public class SafeEnumConverter<T> : StringEnumConverter where T : struct, Enum
{
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
        {
            var enumText = reader.Value?.ToString();
            if (Enum.TryParse(enumText, true, out T parsedEnum))
            {
                return parsedEnum;
            }
        }
        return default(T);
    }
}
