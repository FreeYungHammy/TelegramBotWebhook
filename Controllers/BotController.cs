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
    private readonly DescriptorsService _descriptorsService;

    public BotController(
        ILogger<BotController> logger,
        TelegramBotClient botClient,
        PaymentStatusService paymentService,
        StateService stateService,
        ServerStatusPingService serverStatusService,
        DescriptorsService descriptorsService)
    {
        _logger = logger;
        _botClient = botClient;
        _paymentService = paymentService;
        _stateService = stateService;
        _serverStatusService = serverStatusService;
        _descriptorsService = descriptorsService;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        _logger.LogInformation("Incoming request to /api/bot");

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        _logger.LogDebug("Raw JSON: {Json}", json);

        var chatIdMatch = Regex.Match(json, "\\\"chat\\\":\\\"?\\\"{\\\"id\\\":(-?\\d+)");
        var dataMatch = Regex.Match(json, "\\\"data\\\":\\\"(.*?)\\\"");
        var callbackIdMatch = Regex.Match(json, "\\\"callback_query\\\":\\\"{\\\"id\\\":\\\"(.*?)\\\"");
        var messageIdMatch = Regex.Match(json, "\\\"message_id\\\":(\\d+)");

        if (chatIdMatch.Success && dataMatch.Success && callbackIdMatch.Success && messageIdMatch.Success)
        {
            long chatId = long.Parse(chatIdMatch.Groups[1].Value);
            string callbackData = dataMatch.Groups[1].Value;
            string callbackId = callbackIdMatch.Groups[1].Value;
            int messageId = int.Parse(messageIdMatch.Groups[1].Value);

            await _botClient.AnswerCallbackQuery(callbackId);
            _logger.LogInformation("Callback data received manually parsed: {Data}", callbackData);

            switch (callbackData)
            {
                case "checkstatus":
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
                    break;

                case "helpinfo":
                    await _botClient.SendMessage(chatId,
                        "*Help Guide*\n\n" +
                        "• Mention @NetsellerStatusBot to trigger interactions.\n" +
                        "• Payment Status returns the status of a payment with specified Order# and Company#\n" +
                        "• Blacklist Service includes blacklisting a user based off either their Phone Number, Email First 6 Card Number Digits or Last 4.\n" +
                        "• Descriptors Service returns the contents of the descriptors file.\n" +
                        "• Server Status returns the status of the server.\n" +
                        "• If you would like to view this help menu, it can be returned through the command: '\\help'",
                        parseMode: ParseMode.Markdown);
                    break;

                case "serverstatus":
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
                    break;

                case "retry_order":
                    _stateService.SetAwaitingOrderId(chatId, _stateService.GetCompanyId(chatId));
                    await _botClient.SendMessage(chatId, "Please enter your Order#.");
                    break;

                case "cancel_order":
                    _stateService.ClearAwaitingOrderId(chatId);
                    await _botClient.SendMessage(chatId, "No problem! You can always mention @NetsellerSupportBot if you would like to check again.");
                    break;

                case "blacklist_menu":
                    var blacklistOptions = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("Phone Number", "blacklist_phone") },
                        new[] { InlineKeyboardButton.WithCallbackData("Email", "blacklist_email") },
                        new[] { InlineKeyboardButton.WithCallbackData("First 6 (Card Number)", "blacklist_first6") },
                        new[] { InlineKeyboardButton.WithCallbackData("Last 4 (Card Number)", "blacklist_last4") },
                        new[] { InlineKeyboardButton.WithCallbackData("Back", "main_menu") }
                    });

                    await _botClient.EditMessageReplyMarkup(chatId, messageId, blacklistOptions);
                    break;

                case "descriptors":
                    var contents = "The following are the contents of the Descriptors:\n" + await _descriptorsService.GetDescriptorsAsync();
                    await _botClient.SendMessage(chatId, contents);
                    break;

                case "main_menu":
                    var mainKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("Check Payment Status", "checkstatus"),
                            InlineKeyboardButton.WithCallbackData("Server Status", "serverstatus")
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("Descriptors", "descriptors"),
                            InlineKeyboardButton.WithCallbackData("Blacklist", "blacklist_menu")
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("Help", "helpinfo")
                        }
                    });

                    await _botClient.EditMessageReplyMarkup(chatId, messageId, mainKeyboard);
                    break;
            }

            return Ok();
        }

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

        if (update?.Message?.Text != null)
        {
            var chatId = update.Message.Chat.Id;
            var text = update.Message.Text.Trim();

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
                    "• Mention @NetsellerStatusBot to trigger interactions.\n" +
                    "• Payment Status returns the status of a payment with specified Order# and Company#\n" +
                    "• Blacklist Service includes blacklisting a user based off either their Phone Number, Email First 6 Card Number Digits or Last 4.\n" +
                    "• Descriptors Service returns the contents of the descriptors file.\n" +
                    "• Server Status returns the status of the server.\n" +
                    "• If you would like to view this help menu, it can be returned through the command: '\\help'",
                    parseMode: ParseMode.Markdown);
            }
            else if (text.Contains("@NetsellerSupportBot"))
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Check Payment Status", "checkstatus"),
                        InlineKeyboardButton.WithCallbackData("Server Status", "serverstatus")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Descriptors", "descriptors"),
                        InlineKeyboardButton.WithCallbackData("Blacklist", "blacklist_menu")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Help", "helpinfo")
                    }
                });

                await _botClient.SendMessage(chatId, "What would you like to do?", replyMarkup: keyboard);
            }
        }

        return Ok();
    }
}
