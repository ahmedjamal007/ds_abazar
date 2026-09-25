namespace Erp.TelegramBot.Security;

/// <summary>
/// How many link attempts the bot will entertain per minute, across everybody.
///
/// /link is the one command an UNKNOWN sender can use — it has to be, because it is how a manager
/// becomes known. That makes it the only place a stranger can get the bot to do work, and the only
/// place a six-digit code can be guessed at.
///
/// The budget is GLOBAL rather than per-sender, and that is the whole point. A per-sender limit
/// means the bot remembers every Telegram id that ever messaged it, so anyone can grow that
/// dictionary without bound by messaging from new accounts — a rate limiter that is itself the
/// denial of service. One global counter cannot grow at all.
///
/// The trade is real and worth stating: somebody flooding the bot can use up the budget and stop a
/// manager linking for up to a minute. That is a minor inconvenience with an obvious remedy (try
/// again), whereas the alternatives are unbounded memory or an unthrottled guessing oracle.
///
/// Against a six-digit code this is what makes the arithmetic safe. A code lives ten minutes, so an
/// attacker gets at most ten times this budget of guesses out of a million — and only while an
/// administrator happens to have a code outstanding.
/// </summary>
public sealed class LinkThrottle
{
    private readonly int _perMinute;
    private readonly Queue<DateTimeOffset> _recent = new();

    public LinkThrottle(int attemptsPerMinute)
    {
        _perMinute = attemptsPerMinute > 0 ? attemptsPerMinute : 10;
    }

    /// <summary>
    /// Whether to process a link attempt now, counting it if so. A refused attempt is not counted,
    /// so a flood cannot keep the window sliding forward forever and lock the door permanently.
    /// </summary>
    public bool Allow(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - TimeSpan.FromMinutes(1);
        while (_recent.Count > 0 && _recent.Peek() <= cutoff) _recent.Dequeue();

        if (_recent.Count >= _perMinute) return false;

        _recent.Enqueue(now);
        return true;
    }
}
