namespace Erp.TelegramBot.Security;

/// <summary>What the bot should do with an incoming command, decided before anything is executed.</summary>
public enum Access
{
    /// <summary>Run it.</summary>
    Allowed,

    /// <summary>
    /// Not a known administrator. Say NOTHING back.
    ///
    /// Silence is deliberate. Anyone can find a bot and message it; a reply of any kind — even
    /// "you are not authorized" — confirms the bot is real, belongs to something worth probing, and
    /// is listening. An unknown sender gets an audit row and no response at all.
    /// </summary>
    Denied,

    /// <summary>A known administrator going too fast. Tell them, once.</summary>
    RateLimited,
}

/// <summary>
/// The gate every command passes through.
///
/// Deliberately free of Telegram types and of any clock of its own: the caller passes the moment,
/// so the rate limiter can be tested by handing it timestamps instead of sleeping. This is the one
/// class where a mistake means a stranger reading a pharmacy's takings, so it is kept small enough
/// to hold in your head and is tested on its own.
/// </summary>
public sealed class CommandAuthorizer
{
    private readonly IAdminDirectory _admins;
    private readonly int _perMinute;

    // One rolling window per user. Only ever holds known administrators, so an unknown sender
    // flooding the bot cannot grow this dictionary — the directory check happens first.
    private readonly Dictionary<long, Queue<DateTimeOffset>> _recent = [];

    public CommandAuthorizer(IAdminDirectory admins, int commandsPerMinute)
    {
        _admins = admins;
        _perMinute = commandsPerMinute > 0 ? commandsPerMinute : 20;
    }

    /// <summary>
    /// Whether this sender is a known administrator, WITHOUT spending any of their allowance.
    ///
    /// Asked before /link, to tell "a manager linking for the first time" from "a manager who is
    /// already linked and typed /link again". Checking with <see cref="Check"/> would charge them a
    /// command for a question the bot asked itself.
    /// </summary>
    public bool IsKnown(long telegramUserId) => _admins.IsAdmin(telegramUserId);

    /// <summary>
    /// Decides, and counts the command against the sender's allowance when it is allowed.
    ///
    /// A rejected command is NOT counted: someone who trips the limit would otherwise keep it tripped
    /// by continuing to try, and would never be told why their bot went quiet.
    /// </summary>
    public Access Check(long telegramUserId, DateTimeOffset now)
    {
        if (!_admins.IsAdmin(telegramUserId)) return Access.Denied;

        if (!_recent.TryGetValue(telegramUserId, out Queue<DateTimeOffset>? window))
            _recent[telegramUserId] = window = new Queue<DateTimeOffset>();

        DateTimeOffset cutoff = now - TimeSpan.FromMinutes(1);
        while (window.Count > 0 && window.Peek() <= cutoff) window.Dequeue();

        if (window.Count >= _perMinute) return Access.RateLimited;

        window.Enqueue(now);
        return Access.Allowed;
    }
}
