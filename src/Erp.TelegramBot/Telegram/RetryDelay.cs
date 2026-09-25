namespace Erp.TelegramBot.Telegram;

/// <summary>
/// How long to wait after a failed poll.
///
/// A pharmacy's connection drops, Telegram rate-limits, a DNS lookup fails. Retrying immediately in
/// a tight loop turns a brief outage into a pegged CPU and a log file that fills the disk, and it is
/// how a bot gets its token throttled. So: exponential, capped, and jittered.
///
/// The jitter is not decoration. Every till in a chain of pharmacies that loses the same upstream
/// link retries on the same schedule, and they arrive back together in a thundering herd; spreading
/// them means the recovery is not itself another outage.
///
/// A pure function of the failure count, with the randomness passed in, so the curve can be asserted
/// rather than observed.
/// </summary>
public static class RetryDelay
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The wait after <paramref name="consecutiveFailures"/> failures in a row.
    /// </summary>
    /// <param name="jitter">
    /// A fraction in [0,1) from the caller's random source. Shaves up to 20% off the delay, so the
    /// wait is never longer than the plain exponential — a bot must not become slower to recover
    /// because of jitter.
    /// </param>
    public static TimeSpan For(int consecutiveFailures, double jitter)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;

        // Shift rather than Pow, and cap the exponent before it can overflow: a service left running
        // through a week-long outage must not compute an absurd interval or a negative one.
        int steps = Math.Min(consecutiveFailures - 1, 20);
        double seconds = First.TotalSeconds * (1L << steps);
        seconds = Math.Min(seconds, Ceiling.TotalSeconds);

        // NaN first, and not as a formality. NaN fails BOTH comparisons below, so a range check
        // written the obvious way lets it through, and TimeSpan.FromSeconds(NaN) throws — inside the
        // catch block whose whole purpose is to survive failure. The loop would die of its own
        // recovery. Random.NextDouble never returns NaN, but this function does not get to assume
        // who calls it.
        double clamped = double.IsNaN(jitter) ? 0 : jitter < 0 ? 0 : jitter > 1 ? 1 : jitter;
        return TimeSpan.FromSeconds(seconds * (1.0 - 0.2 * clamped));
    }
}
