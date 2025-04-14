using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TelegramBot_v2.Services;

public class Startup
{
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();

        services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.AddDebug();
        });

        services.AddHttpClient<PaymentStatusService>();

        services.AddSingleton<TelegramBotClient>(_ =>
        {
            var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                        ?? throw new Exception("Bot token not set.");
            return new TelegramBotClient(token);
        });

        var storagePath = Path.Combine("/home/site/wwwroot", "group_company_links.txt");
        services.AddSingleton(new StateService(storagePath));

    }


    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger, TelegramBotClient botClient)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseRouting();
        app.UseHttpsRedirection();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers(); // You’ll create a separate controller for /api/bot
        });

        var botUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
                     ?? throw new Exception("PUBLIC_URL not set.");
        var webhookUrl = $"{botUrl}/api/bot";

        botClient.SetWebhook(webhookUrl, allowedUpdates: new[]
        {
            UpdateType.Message,
            UpdateType.CallbackQuery
        }).Wait();

        logger.LogInformation("Webhook set to {WebhookUrl}", webhookUrl);
    }
}
