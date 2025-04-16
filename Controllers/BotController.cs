using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private readonly ServerStatusPingService _serverStatusService;

    public BotController(
        ILogger<BotController> logger,
        TelegramBotClient botClient,
        PaymentStatusService paymentService,
        StateService stateService,
        ServerStatusPingService serverStatusService)
    {
        _logger = logger;
        _botClient = botClient;
        _paymentService = paymentService;
        _stateService = stateService;
        _serverStatusService = serverStatusService;
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

        if (update.CallbackQuery != null)
        {
            var chatId = update.CallbackQuery.Message?.Chat.Id ?? 0;
            var callbackData = update.CallbackQuery.Data;
            var callbackId = update.CallbackQuery.Id;

            await _botClient.AnswerCallbackQuery(callbackId, text: "Loading...");
            _logger.LogInformation("Callback data received: {Data}", callbackData);

            if (callbackData == "checkstatus")
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    _stateService.SetAwaitingOrderId(chatId, companyId);
                    await _botClient.SendMessage(chatId, "Please enter your Order#.");
                }
                else
                {
                    _stateService.SetAwaitingCompanyId(chatId);
                    await _botClient.SendMessage(chatId, "Please enter your Company# to register.");
                }
            }
            else if (callbackData == "helpinfo")
            {
                await _botClient.SendMessage(chatId,
                    "*Help Guide*\n\n" +
                    "• Use `/paymentstatus` to check the status of a payment.\n" +
                    "• You’ll be prompted for your Company# and Order#.\n" +
                    "• Mention the bot (@NetsellerSupportBot) to trigger.",
                    parseMode: ParseMode.Markdown);
            }
            else if (callbackData == "serverstatus")
            {
                await _botClient.SendMessage(chatId, "Checking...");
                await Task.Delay(2000);
                var result = await _serverStatusService.PingAsync();

                if (result.Contains("Operational"))
                {
                    await Task.Delay(1200);
                    await _botClient.SendMessage(chatId, "Processing Services: Fully Operational");
                    await Task.Delay(1500);
                    await _botClient.SendMessage(chatId, "Report Services: Fully Operational");
                    await Task.Delay(1200);
                    await _botClient.SendMessage(chatId, "Administration Portals: Fully Operational");
                }
            }
            else if (callbackData == "retry_order")
            {
                _stateService.SetAwaitingOrderId(chatId, _stateService.GetCompanyId(chatId));
                await _botClient.SendMessage(chatId, "Please enter your Order#.");
            }
            else if (callbackData == "cancel_order")
            {
                _stateService.ClearAwaitingOrderId(chatId);
                await _botClient.SendMessage(chatId, "No problem! You can always mention @NetsellerSupportBot if you would like to check again.");
                await Task.Delay(2000);
                await _botClient.SendMessage(chatId, "Or you could go bug Sean about it...");
                await Task.Delay(3000);
                await _botClient.SendMessage(chatId, "Your choice.");
            }
            else if (callbackData == "blacklist_menu")
            {
                var blacklistOptions = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("Phone Number", "blacklist_phone") },
                    new[] { InlineKeyboardButton.WithCallbackData("Email", "blacklist_email") },
                    new[] { InlineKeyboardButton.WithCallbackData("First 6 (Card Number)", "blacklist_first6") },
                    new[] { InlineKeyboardButton.WithCallbackData("Last 4 (Card Number)", "blacklist_last4") }
                });

                await _botClient.SendMessage(chatId, "Select the method you'd like to use to blacklist a user:", replyMarkup: blacklistOptions);
            }

            return Ok();
        }

        if (update.Type == UpdateType.Message && update.Message?.Text != null)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var text = message.Text.Trim();

            _logger.LogInformation("Received message from {ChatId}: {Text}", chatId, text);

            if (_stateService.IsWaitingForCompanyId(chatId))
            {
                _stateService.RegisterCompanyId(chatId, text);
                _stateService.SetAwaitingOrderId(chatId, text);
                await _botClient.SendMessage(chatId, $"Company# '{text}' registered. Now, please enter your Order#.");
                return Ok();
            }

            if (_stateService.IsWaitingForOrderId(chatId))
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    var (found, result) = await _paymentService.QueryPaymentStatusAsync(companyId, text);
                    await _botClient.SendMessage(chatId, result);

                    if (found)
                    {
                        _stateService.ClearAwaitingOrderId(chatId);
                    }
                    else
                    {
                        if (text.ToLower().Contains("sean"))
                        {
                            await _botClient.SendMessage(chatId, "Very funny. Try entering a real Order# now.");
                        }

                        var retryButtons = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("Yes", "retry_order"),
                                InlineKeyboardButton.WithCallbackData("No", "cancel_order")
                            }
                        });

                        await _botClient.SendMessage(chatId, "Would you like to try entering the Order# again?", replyMarkup: retryButtons);
                    }
                }
                else
                {
                    await _botClient.SendMessage(chatId, "No Company# found. Please register first.");
                }

                return Ok();
            }

            if (text.StartsWith("/paymentstatus"))
            {
                var companyId = _stateService.GetCompanyId(chatId);
                if (companyId != null)
                {
                    _stateService.SetAwaitingOrderId(chatId, companyId);
                    await _botClient.SendMessage(chatId, "Please enter your Order#.");
                }
                else
                {
                    _stateService.SetAwaitingCompanyId(chatId);
                    await _botClient.SendMessage(chatId, "Please enter your Company# to register.");
                }
            }
            else if (text.StartsWith("/help"))
            {
                await _botClient.SendMessage(chatId,
                    "*Help Guide*\n\n" +
                    "• Use `/paymentstatus` to check the status of a payment.\n" +
                    "• You’ll be prompted for your Company# and Order#.\n" +
                    "• Mention the bot (@NetsellerSupportBot) to trigger.",
                    parseMode: ParseMode.Markdown);
            }
            else if (text.Contains("@NetsellerSupportBot"))
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Check Payment Status", "checkstatus"),
                        InlineKeyboardButton.WithCallbackData("Help", "helpinfo")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Server Status", "serverstatus"),
                        InlineKeyboardButton.WithCallbackData("Blacklist", "blacklist_menu")
                    }
                });

                await _botClient.SendMessage(chatId, "What would you like to do?", replyMarkup: keyboard);
            }
        }

        return Ok();
    }
}
