namespace Erp.TelegramBot.Configuration;

/// <summary>
/// Everything the bot is configured with.
///
/// The token is NOT stored here in production. It comes from the environment variable
/// ERP_TELEGRAM_TOKEN, and the <see cref="Token"/> property is an appsettings fallback for working
/// on a developer's machine. A bot token is a bearer credential: anyone holding it can post as the
/// pharmacy, read every message sent to it, and cannot be stopped except by revoking the token in
/// BotFather. It must never reach the repository.
///
/// In phase 2 the admin page takes over, encrypting the token machine-scoped so the Windows Service
/// can read it back — see the README.
/// </summary>
public sealed class BotOptions
{
    public const string Section = "Bot";

    /// <summary>Local-development fallback only. Leave empty and set ERP_TELEGRAM_TOKEN instead.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Numeric Telegram user IDs allowed to use the bot, by hand, until phase 2's linking tables
    /// exist. Also how the FIRST administrator ever gets in, since there is nobody to approve them.
    /// </summary>
    public long[] AdminTelegramUserIds { get; set; } = [];

    /// <summary>
    /// How long each long poll holds the connection open. Telegram allows up to 50; 30 keeps the
    /// bot responsive while making roughly two requests a minute when nothing is happening.
    /// </summary>
    public int PollTimeoutSeconds { get; set; } = 30;

    /// <summary>Commands per minute per administrator, after which they are told to slow down.</summary>
    public int RateLimitPerMinute { get; set; } = 20;

    /// <summary>
    /// Link attempts per minute across EVERYBODY. Global on purpose: /link is the one command an
    /// unknown sender may use, and a per-sender limit would have the bot remember every Telegram id
    /// that ever messaged it — a rate limiter that is itself the denial of service.
    /// </summary>
    public int LinkAttemptsPerMinute { get; set; } = 10;

    /// <summary>
    /// The folder holding the pharmacy's dawaii.ini — normally its install directory, e.g.
    /// C:\Program Files\Dawaii. The bot runs from its own folder, so it has to be told.
    ///
    /// Empty means "assume a default local install", which is right for the common case and wrong in
    /// network mode. The service logs which it used at startup, because a bot silently reading the
    /// wrong database would report cheerfully empty stock.
    /// </summary>
    public string ErpDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The bot's own SQLite file. Empty uses BotStore.DefaultPath, beside the pharmacy's database
    /// under %ProgramData% so the desktop app and this service reach the same file.
    /// </summary>
    public string BotDatabasePath { get; set; } = string.Empty;

    // ---------------- the outbox (phase 4) ----------------

    /// <summary>
    /// How often the outbox is checked. Five seconds: an alert is worth having promptly, and an
    /// empty-queue check is one cheap local query.
    /// </summary>
    public int OutboxPollSeconds { get; set; } = 5;

    /// <summary>
    /// How many failed deliveries before a message is left alone.
    ///
    /// It is NOT marked sent at that point — it was not sent — so it stays visible as a stuck row
    /// with its last error. Without a cap, a message Telegram will never accept is retried every
    /// five seconds for the life of the installation.
    /// </summary>
    public int OutboxMaxAttempts { get; set; } = 12;

    /// <summary>Whether to send the daily low-stock digest at all.</summary>
    public bool LowStockDigest { get; set; } = true;

    /// <summary>Hour of the day (0-23, local) at or after which the digest is sent.</summary>
    public int LowStockDigestHour { get; set; } = 9;

    /// <summary>
    /// How many drugs the digest names. The rest are a count, with /low for the full list: forty
    /// names arriving unprompted is how a manager learns to stop reading the bot.
    /// </summary>
    public int LowStockDigestNames { get; set; } = 8;

    public TimeSpan OutboxPollInterval =>
        TimeSpan.FromSeconds(OutboxPollSeconds is > 0 and <= 300 ? OutboxPollSeconds : 5);

    public TimeSpan PollTimeout =>
        TimeSpan.FromSeconds(PollTimeoutSeconds is > 0 and <= 50 ? PollTimeoutSeconds : 30);
}
