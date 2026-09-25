using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Security;
using Erp.TelegramBot.Telegram;
using Erp.TelegramBot.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Erp.TelegramBot;

/// <summary>
/// Dawaii's Telegram bot for the pharmacy manager (phase 1).
///
/// One executable that runs either as a console app — for setting it up and watching it work — or as
/// a Windows Service, which is how it runs in a pharmacy: headless, starting with the machine,
/// nobody logged in. It is deliberately NOT part of the desktop application: the till gets closed
/// at the end of a shift and the manager still wants alerts.
///
/// Phase 1 touches no pharmacy data whatsoever. It proves the whole path works — token, connection,
/// long polling, authorization, a reply — before anything is allowed near the database.
/// </summary>
public static class Program
{
    /// <summary>Where the token comes from in production. Never a literal in this repository.</summary>
    public const string TokenVariable = "ERP_TELEGRAM_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // Lets the same binary be `sc.exe create`d. Harmless when started from a console.
        builder.Services.AddWindowsService(o => o.ServiceName = "DawaiiTelegramBot");

        if (WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddEventLog(o => o.SourceName = "Dawaii Telegram Bot");

        builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.Section));

        string token = ResolveToken(builder);
        if (token.Length == 0)
        {
            // Refuse to start rather than run a loop that can never succeed. A service that appears
            // to be running but silently cannot connect is worse than one that will not start: the
            // Event Log entry says exactly what is wrong.
            Console.Error.WriteLine(
                $"No bot token. Set the {TokenVariable} environment variable, " +
                "or Bot:Token in appsettings.json for local development only.");
            return 2;
        }

        builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));
        builder.Services.AddSingleton<ITelegramGateway, TelegramGateway>();

        builder.Services.AddSingleton<IAdminDirectory>(sp =>
        {
            BotOptions o = sp.GetRequiredService<IOptions<BotOptions>>().Value;
            var directory = new ConfiguredAdminDirectory(o.AdminTelegramUserIds);

            if (directory.Count == 0)
            {
                // Started but useless: it will poll, receive, and answer nobody. Worth saying loudly,
                // because the symptom on the phone ("the bot ignores me") gives no hint of the cause.
                sp.GetRequiredService<ILogger<ConfiguredAdminDirectory>>().LogWarning(
                    "No administrators configured. Every command will be ignored. " +
                    "Add numeric Telegram user IDs under Bot:AdminTelegramUserIds — see the README.");
            }
            return directory;
        });

        builder.Services.AddSingleton(sp =>
        {
            BotOptions o = sp.GetRequiredService<IOptions<BotOptions>>().Value;
            return new CommandAuthorizer(sp.GetRequiredService<IAdminDirectory>(), o.RateLimitPerMinute);
        });

        builder.Services.AddSingleton<CommandRouter>();
        builder.Services.AddHostedService<CommandWorker>();

        // Phase 4 adds the second hosted service, NotificationWorker, in this same process.

        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>
    /// Environment variable first, appsettings second.
    ///
    /// That order matters: a developer's appsettings value must never quietly override the real token
    /// on a pharmacy's machine. The fallback exists so nobody has to set a system variable just to
    /// try the bot on their own laptop.
    /// </summary>
    private static string ResolveToken(HostApplicationBuilder builder)
    {
        string fromEnvironment = Environment.GetEnvironmentVariable(TokenVariable) ?? string.Empty;
        if (fromEnvironment.Trim().Length > 0) return fromEnvironment.Trim();

        return (builder.Configuration[$"{BotOptions.Section}:Token"] ?? string.Empty).Trim();
    }
}
