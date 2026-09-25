using Erp.TelegramBot.Telegram;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The backoff curve.
///
/// What this guards against is the bot turning a two-minute internet outage into a pegged CPU and a
/// log file that fills the disk — and getting its token throttled for hammering Telegram. A pharmacy
/// runs this unattended for months, so the loop has to survive failure quietly.
/// </summary>
[TestFixture]
public class RetryDelayTests
{
    [Test]
    public void NoFailures_MeansNoWait()
    {
        Assert.That(RetryDelay.For(0, 0.5), Is.EqualTo(TimeSpan.Zero));
        Assert.That(RetryDelay.For(-3, 0.5), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void TheWaitGrows_AndIsNeverATightLoop()
    {
        Assert.That(RetryDelay.For(1, 0), Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(RetryDelay.For(2, 0), Is.EqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(RetryDelay.For(3, 0), Is.EqualTo(TimeSpan.FromSeconds(8)));

        Assert.That(RetryDelay.For(1, 1), Is.GreaterThan(TimeSpan.FromSeconds(1)),
            "even the first retry, fully jittered, must be slow enough not to spin");
    }

    [Test]
    public void TheWaitIsCapped_SoARecoveredLinkIsNoticedWithinMinutes()
    {
        Assert.That(RetryDelay.For(50, 0), Is.EqualTo(RetryDelay.Ceiling));
        Assert.That(RetryDelay.For(int.MaxValue, 0), Is.EqualTo(RetryDelay.Ceiling),
            "a service left running through a week-long outage must not compute an absurd interval");
    }

    [Test]
    public void ALongOutage_NeverProducesANegativeOrZeroWait()
    {
        // The overflow trap this is written against: shifting by too much wraps, which can give a
        // negative delay, which Task.Delay rejects — crashing the very loop that exists to survive
        // failures. Checked across a range rather than at one point.
        for (int failures = 1; failures < 200; failures++)
            Assert.That(RetryDelay.For(failures, 0.99), Is.GreaterThan(TimeSpan.Zero),
                "failure number " + failures);
    }

    [Test]
    public void JitterSpreadsTheHerd_ButOnlyDownwards()
    {
        TimeSpan plain = RetryDelay.For(4, 0);          // 16s
        TimeSpan jittered = RetryDelay.For(4, 1);

        Assert.That(jittered, Is.LessThan(plain));
        Assert.That(jittered, Is.GreaterThanOrEqualTo(plain * 0.8),
            "jitter exists to spread retries apart, not to make recovery slower than the plain curve");
    }

    [TestCase(-5.0)]
    [TestCase(1.5)]
    [TestCase(double.NaN)]
    public void ANonsenseJitterValue_IsClampedRatherThanTrusted(double jitter)
    {
        TimeSpan wait = RetryDelay.For(3, jitter);

        Assert.That(wait, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(wait, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(8)));
    }
}
