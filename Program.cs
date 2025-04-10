using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register TelegramBotClient
builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();

// Set up in-memory storage for pending order IDs and company registration statuses
var awaitingOrderId = new ConcurrentDictionary<long, string>();
var awaitingCompanyId = new ConcurrentDictionary<long, bool>();

// Define paths for file storage
string localPath = Path.Combine(Directory.GetCurrentDirectory(), "group_company_links.txt"); // Local development
string azurePath = Path.Combine("/home/site/wwwroot", "group_company_links.txt"); // Azure environment

// Load company data from file if available
var groupCompanyMap = new ConcurrentDictionary<long, string>(
    File.Exists(azurePath)
        ? File.ReadAllLines(azurePath)
              .Select(line => line.Split(','))
              .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
              .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
        : new Dictionary<long, string>()
);

app.MapPost("/bot", async context =>
{
    Console.WriteLine("Incoming request to /bot");

    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update = null;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        update = JsonConvert.DeserializeObject<Update>(json);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Deserialization failed: " + ex.Message);
        context.Response.StatusCode = 400;
        return;
    }

    if (update?.Message == null || update.Message.Text == null)
        return;

    var chat = update.Message.Chat;
    var text = update.Message.Text.Trim();
    var chatId = chat.Id;

    if (text.ToLower().Contains("/paymentstatus"))
    {
        awaitingOrderId[chatId] = chatId.ToString();
        await botClient.SendMessage(chatId, "What’s the Order ID?");
        return;
    }

    if (awaitingOrderId.ContainsKey(chatId))
    {
        awaitingOrderId.Remove(chatId, out _);
        var result = await QueryPaymentApiAsync(chatId.ToString(), text);
        await botClient.SendMessage(chatId, result);
        return;
    }

    if (text.Contains("@StatusPaymentBot"))
    {
        if (groupCompanyMap.TryGetValue(chatId, out var companyId))
        {
            awaitingOrderId[chatId] = companyId;
            await botClient.SendMessage(chatId, "What’s the Order ID?");
        }
        else
        {
            awaitingCompanyId[chatId] = true;
            await botClient.SendMessage(chatId, "Group not registered. Please reply with your Company ID to register.");
        }
    }
});

app.MapGet("/setwebhook", async context =>
{
    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    var domain = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? throw new Exception("PUBLIC_URL not set");
    var webhookUrl = $"{domain}/bot";
    await botClient.SetWebhook(webhookUrl);
    await context.Response.WriteAsync("Webhook set!");
});

// Use Swagger and SwaggerUI for Development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();

// API Call Logic
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
            return $"Order {orderId}: No data was found for this transaction";
        }

        var data = result.Data[0];
        return $"Order: {orderId}\nDate: {data.Trans_date}\nStatus: {data.ReplyDesc}\nClient: {data.Client_fullName}\nEmail: {data.Client_email}\n";
    }
    catch (Exception e)
    {
        Console.WriteLine("An error occured: " + e.Message);
        return "Failed to retrieve payment status.";
    }
}

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
