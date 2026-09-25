using Dawaii.Core.Models;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The /low list, and its paging.
///
/// The paging is not cosmetic. Telegram REFUSES any message over 4096 characters — it does not
/// truncate it — so a pharmacy with sixty drugs below their threshold would get nothing at all, and
/// see a bot that ignored them. The feature would break precisely at the pharmacies that need it
/// most, which is why the page size is tested against the limit rather than chosen by eye.
/// </summary>
[TestFixture]
public class LowStockReportTests
{
    private static LowStockLine Line(string name, int available, int reorder)
        => new(new Item
        {
            NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10,
            MinQuantity = reorder, IsActive = true
        }, available);

    private static List<LowStockLine> Many(int count)
        => Enumerable.Range(1, count)
            .Select(i => Line("drug-" + i, i * 10, 500))
            .ToList();

    // ---------------- nothing to report ----------------

    [Test]
    public void AHealthyPharmacy_IsToldSoPlainly()
    {
        LowStockPage page = LowStockReport.Page([], 0);

        Assert.That(page.Text, Does.Contain("لا يوجد"));
        Assert.That(page.PageCount, Is.EqualTo(1));
        Assert.That(page.HasNext, Is.False);
        Assert.That(page.HasPrevious, Is.False);
    }

    // ---------------- one page ----------------

    [Test]
    public void AShortList_IsOnePage_WithNoButtons()
    {
        LowStockPage page = LowStockReport.Page(Many(4), 0);

        Assert.That(page.PageCount, Is.EqualTo(1));
        Assert.That(page.HasNext, Is.False);
        Assert.That(page.Text, Does.Not.Contain("صفحة"), "no page counter when there is one page");
    }

    [Test]
    public void TheCountIsStated_SoTheManagerKnowsTheScale()
    {
        Assert.That(LowStockReport.Page(Many(23), 0).Text, Does.Contain("23"));
    }

    [Test]
    public void ItemsOutOfStock_AreCountedSeparatelyAndMarked()
    {
        var lines = new List<LowStockLine>
        {
            Line("gone", 0, 500),
            Line("low", 100, 500),
        };

        LowStockPage page = LowStockReport.Page(lines, 0);

        Assert.That(page.Text, Does.Contain("نفد تماماً"),
            "out of stock is a different problem from merely low, and needs acting on first");
        Assert.That(page.Text, Does.Contain("⛔"));
    }

    [Test]
    public void EachLineShowsWhatIsLeftAndTheReorderLevel()
    {
        LowStockPage page = LowStockReport.Page([Line("panadol", 240, 500)], 0);

        Assert.That(page.Text, Does.Contain("panadol"));
        Assert.That(page.Text, Does.Contain("2 علبة"), "240 units is 2 boxes and 4 strips");
        Assert.That(page.Text, Does.Contain("4 شريط"));
        Assert.That(page.Text, Does.Contain("حد الطلب"));
    }

    // ---------------- paging ----------------

    [Test]
    public void ALongList_IsSplitIntoPages()
    {
        List<LowStockLine> lines = Many(25);

        LowStockPage first = LowStockReport.Page(lines, 0);
        Assert.That(first.PageCount, Is.EqualTo(3), "25 items at 10 a page");
        Assert.That(first.PageIndex, Is.Zero);
        Assert.That(first.HasPrevious, Is.False);
        Assert.That(first.HasNext, Is.True);

        LowStockPage last = LowStockReport.Page(lines, 2);
        Assert.That(last.HasNext, Is.False);
        Assert.That(last.HasPrevious, Is.True);
    }

    [Test]
    public void EachPageShowsWhichPageItIs()
    {
        Assert.That(LowStockReport.Page(Many(25), 1).Text, Does.Contain("صفحة 2"));
    }

    [Test]
    public void PagesDoNotOverlapAndNothingIsLost()
    {
        List<LowStockLine> lines = Many(25);

        var seen = new List<string>();
        for (int p = 0; p < 3; p++)
        {
            string text = LowStockReport.Page(lines, p).Text;
            seen.AddRange(lines.Select(l => l.Item.NameEn).Where(n => text.Contains(n + "\n")));
        }

        Assert.That(seen.Distinct().Count(), Is.EqualTo(25),
            "every item appears, and none twice — a reorder list that silently drops rows is worse than none");
    }

    [TestCase(-5)]
    [TestCase(99)]
    public void AStaleOrForgedPageNumber_IsClampedRatherThanThrowing(int requested)
    {
        // A button from an older, longer list is a normal thing to receive: the manager left the
        // message in their chat and pressed it an hour later. The page number also arrives from a
        // client, so it is untrusted.
        LowStockPage page = LowStockReport.Page(Many(25), requested);

        Assert.That(page.PageIndex, Is.InRange(0, 2));
        Assert.That(page.Text, Is.Not.Empty);
    }

    // ---------------- the limit that made paging necessary ----------------

    [Test]
    public void AFullPage_FitsInOneTelegramMessage_EvenWithLongArabicNames()
    {
        // The worst realistic case: a full page of drugs with long names and awkward quantities.
        var lines = Enumerable.Range(0, LowStockReport.PageSize)
            .Select(i => new LowStockLine(new Item
            {
                NameEn = "paracetamol-plus-caffeine-extra-strength-" + i,
                GenericName = "باراسيتامول وكافيين مركب " + i,
                UnitsPerStrip = 12, StripsPerBox = 7, MinQuantity = 999, IsActive = true
            }, 877))
            .ToList();

        string text = LowStockReport.Page(lines, 0).Text;

        Assert.That(text.Length, Is.LessThan(CommandRouter.TelegramTextLimit),
            "if a page can overflow, the send fails and the manager sees nothing");
    }

    [Test]
    public void AVeryLongListStillPagesRatherThanGrowing()
    {
        // 300 drugs below threshold would be ~30 pages; what must not happen is one enormous message.
        List<LowStockLine> lines = Many(300);

        for (int p = 0; p < 30; p++)
            Assert.That(LowStockReport.Page(lines, p).Text.Length,
                Is.LessThan(CommandRouter.TelegramTextLimit), $"page {p}");
    }
}
