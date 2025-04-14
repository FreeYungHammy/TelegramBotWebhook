using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBot_v2.Services;
using Newtonsoft.Json.Converters;

[ApiController]
[Route("api/[controller]")]
public class BotController : ControllerBase
{
    private readonly ILogger<BotController> _logger;
    private readonly TelegramBotClient _botClient;
    private readonly PaymentStatusService _paymentService;
    private readonly StateService _stateService;

    public BotController(
        ILogger<BotController> logger,
        TelegramBotClient botClient,
        PaymentStatusService paymentService,
        StateService stateService)
    {
        _logger = logger;
        _botClient = botClient;
        _paymentService = paymentService;
        _stateService = stateService;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        _logger.LogInformation("Incoming request to /api/bot");

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        _logger.LogDebug("Raw JSON: {Json}", json);

        var settings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            Converters = { new UnixDateTimeConverter() },
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Error = (sender, args) =>
            {
                _logger.LogWarning("Deserialization error: {ErrorMessage}", args.ErrorContext.Error.Message);
                args.ErrorContext.Handled = true;
            }
        };

        var update = JsonConvert.DeserializeObject<Update>(json, settings);

        if (update == null)
        {
            _logger.LogWarning("Update deserialized as null.");
            return BadRequest();
        }

        _logger.LogInformation("Deserialized Update Type: {Type}", update.Type);

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery == null)
        {
            _logger.LogWarning("Update type was CallbackQuery, but update.CallbackQuery is null. Raw JSON: {Json}", json);
        }

        if (update.Type == UpdateType.Message && update.Message?.Text != null)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var text = message.Text.Trim();

            _logger.LogInformation("Received message from {ChatId}: {Text}", chatId, text);

            // Handle company registration
            if (_stateService.IsWaitingForCompanyId(chatId))
            {
                _stateService.RegisterCompanyId(chatId, text);
                _stateService.SetAwaitingOrderId(chatId, text); 
                await _botClient.SendMessage(chatId, $"Company ID '{text}' registered. Now, please enter your Order ID.");
                return Ok();
            }

            // Handle order ID input
            if (_stateService.IsWaitingForOrderId(chatId))
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    _stateService.ClearAwaitingOrderId(chatId);
                    var result = await _paymentService.QueryPaymentStatusAsync(companyId, text);
                    await _botClient.SendMessage(chatId, result);
                }
                else
                {
                    await _botClient.SendMessage(chatId, "No company ID found. Please register first.");
                }
                return Ok();
            }

            // Handle commands
            if (text.StartsWith("/paymentstatus"))
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    _stateService.SetAwaitingOrderId(chatId, companyId);
                    await _botClient.SendMessage(chatId, "Please enter your Order ID.");
                }
                else
                {
                    _stateService.SetAwaitingCompanyId(chatId);
                    await _botClient.SendMessage(chatId, "Please enter your Company ID to register.");
                }
            }
            else if (text.StartsWith("/help"))
            {
                await _botClient.SendMessage(chatId,
                    "*Help Guide*\n\n" +
                    "• Use `/paymentstatus` to check the status of a payment.\n" +
                    "• You’ll be prompted for your Company ID and Order ID.\n" +
                    "• Mention the bot (@StatusPaymentBot) to trigger interactive buttons.",
                    parseMode: ParseMode.Markdown);
            }
            else if (text.Contains("@StatusPaymentBot"))
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Check Payment Status", "check_status"),
                        InlineKeyboardButton.WithCallbackData("Help", "help_info")
                    }
                });

                await _botClient.SendMessage(chatId, "What would you like to do?", replyMarkup: keyboard);
            }
        }
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
        {
            var callback = update.CallbackQuery;
            var chatId = callback.Message?.Chat.Id ?? 0;

            _logger.LogInformation("Callback query received from {ChatId}: {Data}", chatId, callback.Data);

            if (callback.Data == "check_status")
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    _stateService.SetAwaitingOrderId(chatId, companyId);
                    await _botClient.SendMessage(chatId, "Please enter your Order ID.");
                }
                else
                {
                    _stateService.SetAwaitingCompanyId(chatId);
                    await _botClient.SendMessage(chatId, "Please enter your Company ID to register.");
                }
            }
            else if (callback.Data == "help_info")
            {
                await _botClient.SendMessage(chatId,
                    "*Help Guide*\n\n" +
                    "• Use `/paymentstatus` to check the status of a payment.\n" +
                    "• You’ll be prompted for your Company ID and Order ID.\n" +
                    "• Mention the bot (@StatusPaymentBot) to trigger interactive buttons.",
                    parseMode: ParseMode.Markdown);
            }

            await _botClient.AnswerCallbackQuery(callback.Id);
        }

        return Ok();
    }
}
