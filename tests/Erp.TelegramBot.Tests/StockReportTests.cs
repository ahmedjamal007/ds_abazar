using Dawaii.Core.Models;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// What /stock actually says.
///
/// This is the bot's product. A manager reads these numbers on a phone and reorders from them
/// without being able to see the shelf, so the wording is the feature and is tested as such.
///
/// Quantities are reported in boxes and strips, never in the single tablets the database stores:
/// "1,247 tablets" is a number nobody can picture.
/// </summary>
[TestFixture]
public class StockReportTests
{
    /// <summary>10 tablets a strip, 10 strips a box, reorder at 500 units (5 boxes).</summary>
    private static Item Panadol() => new Item
    {
        Id = 1, NameEn = "panadol", GenericName = "paracetamol",
        UnitsPerStrip = 10, StripsPerBox = 10, MinQuantity = 500, IsActive = true
    };

    private static StockDetail Detail(int units, params BatchLine[] batches)
        => new(Panadol(), units, batches);

    private static BatchLine Batch(int units, DateTime? expiry, string? lot = "LOT-1")
        => new(units, 10, 10, expiry, lot);

    // ---------------- the quantity ----------------

    [Test]
    public void TheQuantityIsReportedInBoxesAndStrips()
    {
        string text = StockReport.One(Detail(1240, Batch(1240, DateTime.Today.AddYears(1))));

        Assert.That(text, Does.Contain("12 علبة"));
        Assert.That(text, Does.Contain("4 شريط"));
    }

    [Test]
    public void TheExactUnitTotalIsStillGiven()
    {
        // The figure that reconciles with every other screen in the program. Somebody will eventually
        // need to check the bot against the stock list, and a rounded answer makes that impossible.
        string text = StockReport.One(Detail(1247, Batch(1247, DateTime.Today.AddYears(1))));

        Assert.That(text, Does.Contain("1,247"));
        Assert.That(text, Does.Contain("7 حبة"), "and the loose tablets are shown");
    }

    [Test]
    public void TheReorderLevelIsShownTheSameWay_SoTheTwoCanBeCompared()
    {
        string text = StockReport.One(Detail(1240, Batch(1240, DateTime.Today.AddYears(1))));

        Assert.That(text, Does.Contain("حد الطلب"));
        Assert.That(text, Does.Contain("5 علبة"), "500 units is 5 boxes, not '500'");
    }

    [Test]
    public void BeingAtOrBelowTheReorderLevel_IsCalledOut()
    {
        string text = StockReport.One(Detail(500, Batch(500, DateTime.Today.AddYears(1))));

        Assert.That(text, Does.Contain("حد الطلب أو أقل"),
            "a manager scanning a reply must not have to compare two numbers themselves");
    }

    [Test]
    public void BeingOutOfStock_SaysSoPlainly()
    {
        string text = StockReport.One(Detail(0));

        Assert.That(text, Does.Contain("نفد"));
        Assert.That(text, Does.Contain("صفر"));
    }

    [Test]
    public void ComfortableStock_IsNotWarnedAbout()
    {
        string text = StockReport.One(Detail(5000, Batch(5000, DateTime.Today.AddYears(1))));

        Assert.That(text, Does.Not.Contain("⚠️"));
    }

    // ---------------- the batch breakdown ----------------

    [Test]
    public void BatchesAreListedWithTheirExpiryDates()
    {
        DateTime soon = DateTime.Today.AddDays(40);
        DateTime later = DateTime.Today.AddYears(2);

        string text = StockReport.One(Detail(700, Batch(200, soon, "A1"), Batch(500, later, "B2")));

        Assert.That(text, Does.Contain(soon.ToString("yyyy-MM-dd")));
        Assert.That(text, Does.Contain(later.ToString("yyyy-MM-dd")));
        Assert.That(text, Does.Contain("A1"));
        Assert.That(text, Does.Contain("B2"));
    }

    [Test]
    public void StockExpiringSoon_SaysHowManyDays()
    {
        // The part a manager reacts to. Stock expiring next month is not the same asset as stock
        // expiring in two years, and a single total hides the difference entirely.
        string text = StockReport.One(Detail(200, Batch(200, DateTime.Today.AddDays(40))));

        Assert.That(text, Does.Contain("40 يوم"));
    }

    [Test]
    public void AlreadyExpiredStock_IsMarked()
    {
        string text = StockReport.One(Detail(200, Batch(200, DateTime.Today.AddDays(-3))));

        Assert.That(text, Does.Contain("منتهية"));
    }

    [Test]
    public void ABatchExpiringToday_SaysSo()
    {
        Assert.That(StockReport.One(Detail(50, Batch(50, DateTime.Today))),
            Does.Contain("تنتهي اليوم"));
    }

    [Test]
    public void ABatchWithNoExpiry_IsStillListed()
    {
        string text = StockReport.One(Detail(50, Batch(50, null, null)));

        Assert.That(text, Does.Contain("بدون تاريخ انتهاء"));
    }

    [Test]
    public void ABatchUsesItsOwnPackaging_NotTheCataloguesTodayValue()
    {
        // Older shipments came in different boxes; stock_batches records packaging per shipment for
        // exactly that reason, and reporting a batch with today's catalogue figures would misstate it.
        var oddBatch = new BatchLine(127, 12, 5, DateTime.Today.AddYears(1), "OLD");
        string text = StockReport.One(new StockDetail(Panadol(), 127, new[] { oddBatch }));

        Assert.That(text, Does.Contain("2 علبة"), "60 to a box in that shipment, so 127 is 2 boxes");
    }

    // ---------------- searching ----------------

    [Test]
    public void NothingFound_NamesWhatWasLookedFor()
    {
        Assert.That(StockReport.NotFound("zzz"), Does.Contain("zzz"));
    }

    [Test]
    public void SeveralMatches_ListThemRatherThanGuessing()
    {
        var matches = new[]
        {
            new Item { NameEn = "panadol", IsActive = true },
            new Item { NameEn = "panadol extra", IsActive = true },
        };

        string text = StockReport.Ambiguous("pana", matches);

        Assert.That(text, Does.Contain("panadol"));
        Assert.That(text, Does.Contain("panadol extra"));
        Assert.That(text, Does.Contain("أدق"), "it should say what to do next");
    }

    // ---------------- the message limit ----------------

    [Test]
    public void EvenADrugWithManyBatches_FitsInOneTelegramMessage()
    {
        // A drug received monthly for three years has a lot of batches. If this overflows, the send
        // fails and the manager sees nothing at all.
        BatchLine[] many = Enumerable.Range(0, 40)
            .Select(i => Batch(100, DateTime.Today.AddDays(30 * i), "LOT-" + i))
            .ToArray();

        string text = StockReport.One(Detail(4000, many));

        Assert.That(text.Length, Is.LessThan(CommandRouter.TelegramTextLimit),
            "an over-long reply is not truncated by Telegram, it is refused");
    }
}
