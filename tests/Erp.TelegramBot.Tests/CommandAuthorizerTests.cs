using Erp.TelegramBot.Security;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The gate.
///
/// This is the class where a mistake means a stranger reading a pharmacy's stock levels and daily
/// takings from their phone, so it is tested on its own and tested for what it must REFUSE rather
/// than only for what it must allow.
///
/// The clock is passed in, so the rate limiter is exercised by handing it timestamps instead of
/// sleeping for a minute.
/// </summary>
[TestFixture]
public class CommandAuthorizerTests
{
    private const long Manager = 111222333;
    private const long Stranger = 999888777;

    // Fixed instant: a test must not depend on when it runs.
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(2));

    private static CommandAuthorizer Gate(int perMinute = 20, params long[] admins)
        => new(new ConfiguredAdminDirectory(admins.Length > 0 ? admins : [Manager]), perMinute);

    // ---------------- who gets in ----------------

    [Test]
    public void AConfiguredManager_IsAllowed()
    {
        Assert.That(Gate().Check(Manager, Noon), Is.EqualTo(Access.Allowed));
    }

    [Test]
    public void AnyoneElse_IsDenied()
    {
        Assert.That(Gate().Check(Stranger, Noon), Is.EqualTo(Access.Denied),
            "anyone at all can find a bot and message it");
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    public void AnImpossibleUserId_IsDenied(long id)
    {
        // Zero is what an unset field looks like. The gateway reports 0 for an update with no sender
        // — a service notice, a channel post — and that must never match a whitelist entry.
        Assert.That(Gate().Check(id, Noon), Is.EqualTo(Access.Denied));
    }

    [Test]
    public void AZeroInTheConfiguredList_DoesNotBecomeAWhitelistEntry()
    {
        CommandAuthorizer gate = Gate(20, 0, Manager);

        Assert.That(gate.Check(0, Noon), Is.EqualTo(Access.Denied),
            "a stray 0 in appsettings must not admit every senderless update");
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
    }

    [Test]
    public void WithNobodyConfigured_EverythingIsDenied()
    {
        var gate = new CommandAuthorizer(new ConfiguredAdminDirectory(null), 20);

        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Denied),
            "an unconfigured bot must be useless, never open");
    }

    // ---------------- how fast ----------------

    [Test]
    public void UpToTheLimit_IsAllowed_AndThenRefused()
    {
        CommandAuthorizer gate = Gate(perMinute: 3);

        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.RateLimited));
    }

    [Test]
    public void TheWindowRolls_SoTheAllowanceComesBack()
    {
        CommandAuthorizer gate = Gate(perMinute: 2);

        gate.Check(Manager, Noon);
        gate.Check(Manager, Noon);
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.RateLimited));

        Assert.That(gate.Check(Manager, Noon.AddSeconds(61)), Is.EqualTo(Access.Allowed),
            "a minute later the manager can use their own bot again");
    }

    [Test]
    public void ARefusedCommand_DoesNotCountAgainstTheAllowance()
    {
        CommandAuthorizer gate = Gate(perMinute: 1);

        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
        Assert.That(gate.Check(Manager, Noon.AddSeconds(1)), Is.EqualTo(Access.RateLimited));
        Assert.That(gate.Check(Manager, Noon.AddSeconds(2)), Is.EqualTo(Access.RateLimited));

        // Had the refusals been counted, the window would keep sliding forward and the manager would
        // stay locked out for as long as they kept trying — with no way to tell why.
        Assert.That(gate.Check(Manager, Noon.AddSeconds(61)), Is.EqualTo(Access.Allowed));
    }

    [Test]
    public void TheLimit_IsPerPerson()
    {
        const long second = 444555666;
        CommandAuthorizer gate = Gate(1, Manager, second);

        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.Allowed));
        Assert.That(gate.Check(Manager, Noon), Is.EqualTo(Access.RateLimited));
        Assert.That(gate.Check(second, Noon), Is.EqualTo(Access.Allowed),
            "one manager going too fast must not lock out another");
    }

    [Test]
    public void AStrangerFlooding_IsDeniedWithoutBeingRemembered()
    {
        CommandAuthorizer gate = Gate(perMinute: 2);

        // Denied is decided before the rate limiter on purpose: an unknown sender must never be able
        // to make the bot allocate per-user state, or a flood becomes a memory leak.
        for (int i = 0; i < 500; i++)
            Assert.That(gate.Check(Stranger, Noon), Is.EqualTo(Access.Denied));
    }
}
