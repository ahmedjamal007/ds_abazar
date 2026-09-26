using Dawaii.Core.Abstractions;
using Dawaii.Core.Bot;
using Dawaii.Core.Data;
using Erp.TelegramBot.Commands;
using Dawaii.Core.Services;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Pharmacy;
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
/// Dawaii's Telegram bot for the pharmacy manager.
///
/// One executable that runs either as a console app — for setting it up and watching it work — or as
/// a Windows Service, which is how it runs in a pharmacy: headless, starting with the machine,
/// nobody logged in. Deliberately NOT part of the desktop application: the till gets closed at the
/// end of a shift and the manager still wants to be able to ask about stock.
///
/// It opens TWO databases, and the asymmetry is the whole design:
///   * the pharmacy's, READ-ONLY, only ever to check who is asking and (later) to answer questions;
///   * its own, for links, the token and the audit log.
/// A 24/7 background service that wrote to the pharmacy's file could lock or corrupt the one thing
/// the shop cannot lose.
/// </summary>
public static class Program
{
    /// <summary>
    /// The token, when it is not in the bot's database. Phase 1's mechanism, kept for local
    /// development and for setting the bot up before the admin page has been used.
    /// </summary>
    public const string TokenVariable = "ERP_TELEGRAM_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(o => o.ServiceName = "DawaiiTelegramBot");
        if (WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddEventLog(o => o.SourceName = "Dawaii Telegram Bot");

        builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.Section));
        BotOptions options = Read(builder);

        // ---- the bot's own database ----
        string botPath = options.BotDatabasePath.Trim().Length > 0
            ? options.BotDatabasePath.Trim()
            : BotStore.DefaultPath;

        var botStore = new BotStore(new SqliteConnectionFactory(botPath));
        try
        {
            botStore.EnsureSchema();
        }
        catch (Exception ex)
        {
            // Nothing works without it: no token, no links, no audit. Refuse to start rather than
            // poll forever while unable to authorize anybody.
            Console.Error.WriteLine($"Cannot open the bot database at {botPath}: {ex.Message}");
            return 3;
        }
        builder.Services.AddSingleton(botStore);

        // ---- the token ----
        //
        // The admin page first, the environment variable second. That order is deliberate: once a
        // manager has set the token in the program, that is the authoritative copy, and a stale
        // variable left on the machine from setup must not silently override it.
        string token = botStore.ReadToken() ?? "";
        string source = "the admin page";
        if (token.Trim().Length == 0)
        {
            token = ResolveTokenFromEnvironment(builder);
            source = TokenVariable;
        }

        if (token.Trim().Length == 0)
        {
            Console.Error.WriteLine(
                "No bot token. Set it in Dawaii: Admin page -> Telegram setup, " +
                $"or set the {TokenVariable} environment variable for local development.");
            return 2;
        }

        // ---- the pharmacy's database, read-only in this version ----
        DawaiiInstallation erp = options.ErpDirectory.Trim().Length > 0
            ? DawaiiInstallation.FromDirectory(options.ErpDirectory.Trim())
            : DawaiiInstallation.ForThisProgram();

        IDbConnectionFactory erpDb;
        try
        {
            erpDb = erp.CreateConnectionFactory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot reach the pharmacy database: {ex.Message}");
            return 4;
        }

        builder.Services.AddSingleton(erpDb);
        builder.Services.AddSingleton<IUserRepository>(_ => new SqliteUserRepository(erpDb));

        // The pharmacy's own repositories and services, so the bot's idea of "available" is the till's
        // — sellable batches only, disposed stock excluded, FEFO order. A second implementation of
        // "how much is on the shelf" that drifted from the first would be worse than no bot: the
        // manager would be reading a number nobody else in the building agrees with.
        //
        // The audit repository REFUSES to write. Read-only is made structural rather than left as an
        // intention: a future change that calls a mutating method throws here instead of quietly
        // writing rows to the pharmacy's log under a user who was not there.
        builder.Services.AddSingleton<IItemRepository>(_ => new SqliteItemRepository(erpDb));
        builder.Services.AddSingleton<IStockRepository>(_ => new SqliteStockRepository(erpDb));
        builder.Services.AddSingleton<IItemCodeRepository>(_ => new SqliteItemCodeRepository(erpDb));
        builder.Services.AddSingleton<ISettingsRepository>(_ => new SqliteSettingsRepository(erpDb));
        builder.Services.AddSingleton<IAuditRepository, RefusingAuditRepository>();

        builder.Services.AddSingleton(sp => new InventoryService(
            sp.GetRequiredService<IItemRepository>(),
            sp.GetRequiredService<IStockRepository>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<IAuditRepository>(),
            sp.GetRequiredService<IItemCodeRepository>()));

        // ReportService and PosService are what /sales and /report go through, so the permission
        // rules the pharmacy already has — BestSellers and DeadStock refusing a non-administrator,
        // profit shown only to one — apply to the bot's answers without being restated here.
        builder.Services.AddSingleton<ISaleStore>(_ => new SqliteSaleStore(erpDb));
        builder.Services.AddSingleton<IReportRepository>(_ => new SqliteReportRepository(erpDb));
        builder.Services.AddSingleton<ICustomerRepository>(_ => new SqliteCustomerRepository(erpDb));

        builder.Services.AddSingleton(sp => new ReportService(
            sp.GetRequiredService<ISaleStore>(),
            sp.GetRequiredService<IReportRepository>()));

        builder.Services.AddSingleton(sp => new PosService(
            sp.GetRequiredService<IItemRepository>(),
            sp.GetRequiredService<IStockRepository>(),
            sp.GetRequiredService<ISaleStore>(),
            sp.GetRequiredService<ICustomerRepository>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<IAuditRepository>()));

        builder.Services.AddSingleton(sp => new CodeService(
            sp.GetRequiredService<IItemCodeRepository>(),
            sp.GetRequiredService<IItemRepository>(),
            sp.GetRequiredService<IAuditRepository>()));

        builder.Services.AddSingleton<IPharmacyReader, PharmacyReader>();

        builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token.Trim()));
        builder.Services.AddSingleton<ITelegramGateway, TelegramGateway>();

        builder.Services.AddSingleton<LinkedAdminDirectory>();
        builder.Services.AddSingleton<IAdminDirectory>(sp => sp.GetRequiredService<LinkedAdminDirectory>());
        builder.Services.AddSingleton<ILinkService, LinkService>();

        builder.Services.AddSingleton(sp =>
            new CommandAuthorizer(sp.GetRequiredService<IAdminDirectory>(), options.RateLimitPerMinute));
        builder.Services.AddSingleton(_ => new LinkThrottle(options.LinkAttemptsPerMinute));

        builder.Services.AddSingleton<CommandRouter>();
        builder.Services.AddHostedService<CommandWorker>();

        // Two more hosted services in this same process, deliberately independent of the command
        // loop: a manager typing commands has nothing to do with an alert going out, and neither
        // should be able to stall the other.
        builder.Services.AddHostedService<NotificationWorker>();
        builder.Services.AddHostedService<LowStockAlertWorker>();

        IHost host = builder.Build();

        // Said once, at startup, because every one of these is a thing that fails silently and
        // confusingly if it is wrong: a bot reading the wrong database reports empty stock, and a bot
        // with nobody linked simply ignores its owner.
        ILogger<object> log = host.Services.GetRequiredService<ILogger<object>>();
        log.LogInformation("Token from {Source}.", source);
        log.LogInformation("Bot database: {BotPath}", botPath);
        log.LogInformation("Pharmacy database: {Mode}, config {Found} in {Directory}",
            erp.IsServerMode ? "MySQL (network mode)" : "SQLite " + erp.DatabasePath,
            erp.Found ? "found" : "NOT FOUND — assuming a default local install",
            erp.Directory.Length > 0 ? erp.Directory : "(unset)");

        int linked = 0;
        try { linked = botStore.All().Count(u => u.IsActive); } catch { }
        if (linked == 0)
            log.LogWarning(
                "No Telegram accounts are linked. Every command will be ignored until a manager " +
                "generates a code in Dawaii (Admin page -> Telegram setup) and sends /link <code>.");
        else
            log.LogInformation("{Linked} Telegram account(s) linked.", linked);

        await host.RunAsync();
        return 0;
    }

    private static BotOptions Read(HostApplicationBuilder builder)
    {
        var options = new BotOptions();
        builder.Configuration.GetSection(BotOptions.Section).Bind(options);
        return options;
    }

    /// <summary>
    /// Environment variable first, then appsettings — so a developer's file can never override the
    /// real token on a pharmacy's machine.
    /// </summary>
    private static string ResolveTokenFromEnvironment(HostApplicationBuilder builder)
    {
        string fromEnvironment = Environment.GetEnvironmentVariable(TokenVariable) ?? string.Empty;
        if (fromEnvironment.Trim().Length > 0) return fromEnvironment.Trim();

        return (builder.Configuration[$"{BotOptions.Section}:Token"] ?? string.Empty).Trim();
    }
}
