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

    public TimeSpan PollTimeout =>
        TimeSpan.FromSeconds(PollTimeoutSeconds is > 0 and <= 50 ? PollTimeoutSeconds : 30);
}
