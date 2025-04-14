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
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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

        var settings = new Newtonsoft.Json.JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            Converters = new List<Newtonsoft.Json.JsonConverter> { new UnixDateTimeConverter() },
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore
        };

        update = Newtonsoft.Json.JsonConvert.DeserializeObject<Update>(json, settings);

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
        await botClient.SendMessage(update.CallbackQuery.Message.Chat.Id, $"Callback received: {update.CallbackQuery.Data}");
    }
    else if (update.Message != null && update.Message.Text != null)
    {
        await botClient.SendMessage(update.Message.Chat.Id, $"Message received: {update.Message.Text}");
    }
    else
    {
        logger.LogInformation("No message or callback query content.");
        return;
    }
});

app.Run();

public class UnixDateTimeConverter : Newtonsoft.Json.JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(DateTime) || objectType == typeof(DateTime?);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Integer)
        {
            var seconds = Convert.ToInt64(reader.Value);
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        throw new JsonSerializationException($"Unexpected token parsing date. Expected Integer, got {reader.TokenType}.");
    }

    public override void WriteJson(JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is DateTime dateTime)
        {
            var seconds = new DateTimeOffset(dateTime).ToUnixTimeSeconds();
            writer.WriteValue(seconds);
        }
        else
        {
            throw new JsonSerializationException("Expected DateTime object value.");
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
