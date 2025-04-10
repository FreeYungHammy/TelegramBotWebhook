using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add TelegramBotClient to the services
builder.Services.AddSingleton<TelegramBotClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? throw new Exception("Bot token not set.");
    return new TelegramBotClient(token);
});

var app = builder.Build();

// In-memory conversation state
var awaitingOrderIdFromGroup = new Dictionary<long, string>();
var awaitingCompanyIdFromGroup = new HashSet<long>();

// Webhook endpoint to receive Telegram updates
app.MapPost("/bot", async context =>
{
    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    Update? update = null;

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        update = JsonSerializer.Deserialize<Update>(json);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Deserialization failed: " + ex.Message);
        context.Response.StatusCode = 400;
        return;
    }

    if (update?.Message != null)
    {
        var chatId = update.Message.Chat.Id;
        var messageText = update.Message.Text;

        if (messageText != null)
        {
            if (messageText.StartsWith("/paymentstatus"))
            {
                // Handle payment status command
                await PaymentStatusCommand(update, botClient);
                return;
            }

            // Handle other commands or fallback logic
            await HandleUpdateAsync(botClient, update);
        }
    }

    context.Response.StatusCode = 200;
});

app.MapControllers();

// Webhook setup route (used to set webhook URL)
app.MapGet("/setwebhook", async context =>
{
    var botClient = app.Services.GetRequiredService<TelegramBotClient>();
    var domain = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? throw new Exception("PUBLIC_URL not set");
    var webhookUrl = $"{domain}/bot";  // Your API URL with '/bot' endpoint
    Console.WriteLine("Setting webhook to: " + webhookUrl);
    await botClient.SetWebhook(webhookUrl);
    await context.Response.WriteAsync("Webhook set!");
});

app.Run();

// Payment Status command handling
async Task PaymentStatusCommand(Update update, TelegramBotClient botClient)
{
    var chatId = update.Message.Chat.Id;
    string? companyId = GetCompanyIdFromFile(chatId);

    if (companyId == null)
    {
        await botClient.SendMessage(chatId, "Unregistered group. Please register your company with the system.");
        return;
    }

    awaitingOrderIdFromGroup[chatId] = companyId;
    await botClient.SendMessage(chatId, "What’s the Order ID?");
}

// Handle updates
async Task HandleUpdateAsync(TelegramBotClient botClient, Update update)
{
    if (update.Type != UpdateType.Message || update.Message == null) return;

    var message = update.Message;
    var text = message.Text?.Trim() ?? "";
    var chat = message.Chat;

    if (chat.Type is ChatType.Group or ChatType.Supergroup)
    {
        long groupId = chat.Id;
        string groupTitle = chat.Title ?? "Unknown";

        Console.WriteLine($"Group message from '{groupTitle}' ({groupId}): {text}");

        if (awaitingCompanyIdFromGroup.Contains(groupId))
        {
            string companyId = text;
            SaveGroupCompanyLink(groupId, companyId);
            awaitingCompanyIdFromGroup.Remove(groupId);

            await botClient.SendMessage(groupId, $"Company ID '{companyId}' registered successfully!");
            return;
        }

        // Handle order ID
        if (awaitingOrderIdFromGroup.ContainsKey(groupId))
        {
            var orderId = text;
            var companyId = awaitingOrderIdFromGroup[groupId];
            awaitingOrderIdFromGroup.Remove(groupId);

            string result = await QueryPaymentApiAsync(companyId, orderId);
            await botClient.SendMessage(groupId, $"Payment status for Order {orderId} (Company {companyId}): {result}");
            return;
        }
    }
}

// File reading (to get company ID from file)
string? GetCompanyIdFromFile(long groupId)
{
    var path = "group_company_links.txt";
    if (!System.IO.File.Exists(path)) return null;

    var lines = System.IO.File.ReadAllLines(path);
    foreach (var line in lines)
    {
        var parts = line.Split(',');
        if (parts.Length != 2) continue;

        if (long.TryParse(parts[0], out long fileGroupId) && fileGroupId == groupId)
        {
            return parts[1].Trim();
        }
    }

    return null;
}

// Save group and company link to file
void SaveGroupCompanyLink(long groupId, string companyId)
{
    var path = "group_company_links.txt";
    System.IO.File.AppendAllText(path, $"{groupId},{companyId}{Environment.NewLine}");
}

// API call for payment status
async Task<string> QueryPaymentApiAsync(string companyId, string orderId)
{
    try
    {
        using var client = new HttpClient();
        var url = $"https://process.highisk.com/member/getstatus.asp?CompanyNum={companyId}&Order={orderId}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        var result = JsonSerializer.Deserialize<ApiResponse>(json);

        if (result?.Data == null || result.Data.Count == 0)
        {
            return $"Order {orderId}: No data was found for this transaction";
        }

        var data = result.Data[0];

        return $"Order: {orderId}\n" +
               $"Date: {data.Trans_date}\n" +
               $"Status: {data.ReplyDesc}\n" +
               $"Client: {data.Client_fullName}\n" +
               $"Email: {data.Client_email}";
    }
    catch (Exception e)
    {
        Console.WriteLine("An error occurred: " + e.Message);
        throw;
    }
}

// API Response Model
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
