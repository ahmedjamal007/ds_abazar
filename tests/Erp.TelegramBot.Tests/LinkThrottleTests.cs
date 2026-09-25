using Erp.TelegramBot.Security;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The global budget for link attempts.
///
/// /link is the one command an unknown sender may use, so it is the only place a stranger can make
/// the bot work and the only place a six-digit code can be guessed at. This is what keeps the
/// arithmetic safe: a code lives ten minutes, so an attacker gets at most ten times this budget out
/// of a million — and only while an administrator happens to have a code outstanding.
///
/// Global rather than per-sender, deliberately. A per-sender limit means remembering every Telegram
/// id that ever messaged the bot, which anyone can grow without bound by messaging from new
/// accounts — a rate limiter that is itself the denial of service.
/// </summary>
[TestFixture]
public class LinkThrottleTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(2));

    [Test]
    public void AttemptsAreAllowedUpToTheBudget_ThenRefused()
    {
        var throttle = new LinkThrottle(3);

        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon), Is.False);
    }

    [Test]
    public void TheBudgetIsShared_NotPerSender()
    {
        // The property that makes the bot's memory bounded: nothing here is keyed by who asked, so a
        // flood from a thousand fresh accounts allocates nothing.
        var throttle = new LinkThrottle(2);

        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon), Is.False,
            "a third attempt is refused whoever it came from");
    }

    [Test]
    public void TheWindowRolls_SoAManagerCanAlwaysEventuallyLink()
    {
        var throttle = new LinkThrottle(1);

        Assert.That(throttle.Allow(Noon), Is.True);
        Assert.That(throttle.Allow(Noon.AddSeconds(30)), Is.False);
        Assert.That(throttle.Allow(Noon.AddSeconds(61)), Is.True);
    }

    [Test]
    public void ARefusedAttempt_DoesNotExtendTheLockout()
    {
        var throttle = new LinkThrottle(1);
        Assert.That(throttle.Allow(Noon), Is.True);

        // Somebody hammering it for a full minute.
        for (int second = 1; second <= 59; second++)
            Assert.That(throttle.Allow(Noon.AddSeconds(second)), Is.False);

        // Had refusals been counted, the window would keep sliding and the door would stay shut for
        // as long as the flood continued — locking the manager out indefinitely.
        Assert.That(throttle.Allow(Noon.AddSeconds(61)), Is.True);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void ANonsensicalBudget_FallsBackToADefault_RatherThanBlockingEverything(int configured)
    {
        // A zero in appsettings must not mean "nobody may ever link", which would be an unfixable
        // bot: you cannot link to fix the setting.
        var throttle = new LinkThrottle(configured);

        Assert.That(throttle.Allow(Noon), Is.True);
    }
}
