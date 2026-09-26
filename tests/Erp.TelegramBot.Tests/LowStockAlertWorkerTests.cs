using System.Data.SQLite;
using Dawaii.Core.Bot;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The daily low-stock digest, and specifically its restraint.
///
/// The hard part of this feature was never finding low stock — /low already does that. It is NOT
/// SENDING IT FORTY TIMES. An alert that arrives every five minutes gets muted within a week, and a
/// muted alert is worse than no alert: the manager believes they are being watched over when they
/// are not.
///
/// So the tests that matter are about how often it stays quiet. The state lives in the bot's
/// database rather than in memory, because a Windows Service restarts whenever the PC does and a
/// digest that re-announced itself on every boot would be exactly the spam this guards against.
/// </summary>
[TestFixture]
public class LowStockAlertWorkerTests
{
    private string _path = "";
    private BotStore _bot = null!;
    private TestPharmacy _pharmacy = null!;
    private BotOptions _options = null!;

    /// <summary>9am on a Saturday. The digest hour defaults to 9.</summary>
    private static readonly DateTime Morning = new(2026, 9, 26, 9, 5, 0);

    [SetUp]
    public void SetUp()
    {
        _path = Path.Combine(Path.GetTempPath(), "dawaii_digest_" + Guid.NewGuid().ToString("N") + ".db");
        _bot = new BotStore(new SqliteConnectionFactory(_path));
        _bot.EnsureSchema();

        _pharmacy = new TestPharmacy();
        _options = new BotOptions { LowStockDigest = true, LowStockDigestHour = 9, LowStockDigestNames = 3 };
    }

    [TearDown]
    public void TearDown()
    {
        SQLiteConnection.ClearAllPools();
        GC.Collect(); GC.WaitForPendingFinalizers();
        foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private LowStockAlertWorker Worker() => new(
        _bot, _pharmacy, new OptionsWrapper<BotOptions>(_options),
        NullLogger<LowStockAlertWorker>.Instance);

    private static LowStockLine Line(string name, int available = 10, int reorder = 500)
        => new(new Item
        {
            NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10,
            MinQuantity = reorder, IsActive = true
        }, available);

    private IReadOnlyList<OutboxMessage> Queued() => _bot.Pending(12, 50);

    // ---------------- when it speaks ----------------

    [Test]
    public void AtTheDigestHour_WithLowStock_ItQueuesOneMessage()
    {
        _pharmacy.Low = [Line("panadol"), Line("brufen")];

        Worker().Consider(Morning);

        Assert.That(Queued().Count, Is.EqualTo(1));
        Assert.That(Queued()[0].Audience, Is.EqualTo(OutboxMessage.Admins));
    }

    [Test]
    public void ItOnlyEverQueues_NeverSends()
    {
        // It has no Telegram gateway at all — a network outage is somebody else's problem, and the
        // digest is delivered whenever the connection returns.
        _pharmacy.Low = [Line("panadol")];

        Assert.DoesNotThrow(() => Worker().Consider(Morning));
        Assert.That(Queued().Count, Is.EqualTo(1));
    }

    [Test]
    public void BeforeTheDigestHour_ItStaysQuiet()
    {
        _pharmacy.Low = [Line("panadol")];

        Worker().Consider(new DateTime(2026, 9, 26, 6, 0, 0));

        Assert.That(Queued(), Is.Empty, "nobody wants a stock alert at six in the morning");
    }

    // ---------------- when it stays quiet ----------------

    [Test]
    public void TwiceInOneDay_SendsOnce()
    {
        _pharmacy.Low = [Line("panadol")];
        LowStockAlertWorker worker = Worker();

        worker.Consider(Morning);
        worker.Consider(Morning.AddMinutes(5));
        worker.Consider(Morning.AddHours(6));

        Assert.That(Queued().Count, Is.EqualTo(1),
            "the check runs every five minutes; the alert must not");
    }

    [Test]
    public void ARestartedService_DoesNotReAnnounceTodaysDigest()
    {
        // The case this is really written for: a Windows Service restarts whenever the PC does, and a
        // pharmacy's computer is rebooted often. In-memory state would re-send on every boot.
        _pharmacy.Low = [Line("panadol")];
        Worker().Consider(Morning);

        Worker().Consider(Morning.AddHours(2));     // a brand-new worker, as after a reboot

        Assert.That(Queued().Count, Is.EqualTo(1));
    }

    [Test]
    public void TheNextDay_ItSpeaksAgain()
    {
        _pharmacy.Low = [Line("panadol")];
        LowStockAlertWorker worker = Worker();

        worker.Consider(Morning);
        worker.Consider(Morning.AddDays(1));

        Assert.That(Queued().Count, Is.EqualTo(2), "it is a daily digest, not a one-off");
    }

    [Test]
    public void AHealthyPharmacy_IsNotToldAnything()
    {
        _pharmacy.Low = [];

        Worker().Consider(Morning);

        Assert.That(Queued(), Is.Empty, "nothing wrong is not news");
    }

    [Test]
    public void AHealthyPharmacy_IsNotRecheckedAllDay()
    {
        _pharmacy.Low = [];
        LowStockAlertWorker worker = Worker();

        worker.Consider(Morning);
        worker.Consider(Morning.AddMinutes(5));
        worker.Consider(Morning.AddMinutes(10));

        Assert.That(_pharmacy.LowReads, Is.EqualTo(1),
            "re-querying the pharmacy's database every five minutes to learn nothing is waste");
    }

    [Test]
    public void SwitchedOff_ItQueuesNothing()
    {
        _options.LowStockDigest = false;
        _pharmacy.Low = [Line("panadol")];

        Worker().Consider(Morning);

        Assert.That(Queued(), Is.Empty, "a pharmacy that turned the digest off must not get one");
        Assert.That(_pharmacy.LowReads, Is.Zero, "and its database is not queried for it either");
    }

    // ---------------- what it says ----------------

    [Test]
    public void TheDigestSummarises_RatherThanListingEverything()
    {
        _pharmacy.Low = Enumerable.Range(1, 40).Select(i => Line("drug-" + i)).ToList();

        Worker().Consider(Morning);
        string body = Queued()[0].Body;

        Assert.That(body, Does.Contain("40"), "the scale is stated");
        Assert.That(body, Does.Contain("drug-1"), "and the worst few are named");
        Assert.That(body, Does.Not.Contain("drug-9"), "but not forty names at eight in the morning");
        Assert.That(body, Does.Contain("/low"), "with a way to see the rest");
    }

    [Test]
    public void ItemsOutOfStock_AreCalledOutSeparately()
    {
        _pharmacy.Low = [Line("gone", available: 0), Line("low", available: 100)];

        Worker().Consider(Morning);

        Assert.That(Queued()[0].Body, Does.Contain("نفد تماماً"),
            "out of stock is a different problem from merely low");
    }

    [Test]
    public void TheDigestFitsInOneTelegramMessage()
    {
        _pharmacy.Low = Enumerable.Range(1, 500)
            .Select(i => Line("paracetamol-plus-caffeine-extra-strength-" + i))
            .ToList();

        Worker().Consider(Morning);

        Assert.That(Queued()[0].Body.Length,
            Is.LessThan(Erp.TelegramBot.Commands.CommandRouter.TelegramTextLimit),
            "an over-long message is refused by Telegram, not truncated");
    }
}
