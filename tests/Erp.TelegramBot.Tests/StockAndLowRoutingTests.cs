using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using Erp.TelegramBot.Telegram;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// Routing /stock and /low, and the buttons under the list.
///
/// The reports themselves are tested elsewhere; what matters here is the branching a manager
/// actually walks into — a barcode that resolves, a name that matches nothing, a name that matches
/// several — and the paging, where the data comes back from a CLIENT and cannot be trusted.
/// </summary>
[TestFixture]
public class StockAndLowRoutingTests
{
    private sealed class NoLinking : ILinkService
    {
        public LinkOutcome Redeem(string code, long telegramUserId, long chatId) => LinkOutcome.NoSuchCode;
    }

    private TestPharmacy _pharmacy;
    private CommandRouter _router;
    private static readonly Sender Manager = new(111222333, 111222333);

    [SetUp]
    public void SetUp()
    {
        _pharmacy = new TestPharmacy();
        _router = new CommandRouter(new NoLinking(), _pharmacy);
    }

    private Reply? Route(string text) => _router.Handle(CommandLine.Parse(text), Manager);

    private static Item Drug(string name = "panadol") => new Item
    {
        Id = 1, NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10,
        MinQuantity = 500, IsActive = true
    };

    private static LowStockLine Line(int i) => new(new Item
    {
        NameEn = "drug-" + i, UnitsPerStrip = 10, StripsPerBox = 10, MinQuantity = 500, IsActive = true
    }, i * 10);

    // ---------------- /stock ----------------

    [Test]
    public void StockWithNoArgument_ExplainsHowToUseIt()
    {
        Assert.That(Route("/stock")!.Text, Does.Contain("/stock panadol"));
        Assert.That(_pharmacy.LastQuery, Is.Null, "nothing was looked up");
    }

    [Test]
    public void StockPassesTheWholeQuery_BecauseDrugNamesHaveSpaces()
    {
        _pharmacy.One = new StockDetail(Drug(), 1000, []);

        Route("/stock panadol extra 500");

        Assert.That(_pharmacy.LastQuery, Is.EqualTo("panadol extra 500"),
            "taking only the first word would make half the catalogue unsearchable");
    }

    [Test]
    public void AResolvedDrug_IsReported()
    {
        _pharmacy.One = new StockDetail(Drug(), 1240, []);

        Reply? reply = Route("/stock 12345");

        Assert.That(reply!.Text, Does.Contain("panadol"));
        Assert.That(reply.Text, Does.Contain("12 علبة"));
        Assert.That(reply.Buttons, Is.Null, "a single drug needs no buttons");
    }

    [Test]
    public void NothingMatching_SaysSo()
    {
        _pharmacy.One = null;
        _pharmacy.Matches = [];

        Assert.That(Route("/stock zzz")!.Text, Does.Contain("zzz"));
    }

    [Test]
    public void SeveralMatches_AreListedRatherThanPickingOne()
    {
        // The distinction matters: nothing found means scan the barcode, several found means type
        // more of the name. Answering both the same way leaves the manager guessing.
        _pharmacy.One = null;
        _pharmacy.Matches = [Drug("panadol"), Drug("panadol extra")];

        string text = Route("/stock pana")!.Text;

        Assert.That(text, Does.Contain("panadol extra"));
        Assert.That(text, Does.Contain("أدق"));
    }

    // ---------------- /low and its buttons ----------------

    [Test]
    public void LowWithNothingBelowThreshold_SaysTheStockIsFine()
    {
        Reply? reply = Route("/low");

        Assert.That(reply!.Text, Does.Contain("لا يوجد"));
        Assert.That(reply.Buttons, Is.Null);
    }

    [Test]
    public void AShortLowList_HasNoButtons()
    {
        _pharmacy.Low = Enumerable.Range(1, 4).Select(Line).ToList();

        Assert.That(Route("/low")!.Buttons, Is.Null,
            "a button that does nothing reads on a phone as a bot that stopped responding");
    }

    [Test]
    public void ALongLowList_OffersNextButNotPrevious()
    {
        _pharmacy.Low = Enumerable.Range(1, 25).Select(Line).ToList();

        IReadOnlyList<Button> buttons = Route("/low")!.Buttons!;

        Assert.That(buttons.Count, Is.EqualTo(1), "there is nowhere back from the first page");
        Assert.That(buttons[0].Data, Is.EqualTo(CommandRouter.LowPagePrefix + "1"));
    }

    [Test]
    public void PressingNext_MovesOnAndOffersBoth()
    {
        _pharmacy.Low = Enumerable.Range(1, 25).Select(Line).ToList();

        Reply? second = _router.HandlePress(CommandRouter.LowPagePrefix + "1");

        Assert.That(second!.Text, Does.Contain("صفحة 2"));
        Assert.That(second.Buttons!.Count, Is.EqualTo(2), "back and forward from the middle");
    }

    [Test]
    public void TheLastPage_OffersOnlyPrevious()
    {
        _pharmacy.Low = Enumerable.Range(1, 25).Select(Line).ToList();

        IReadOnlyList<Button> buttons = _router.HandlePress(CommandRouter.LowPagePrefix + "2")!.Buttons!;

        Assert.That(buttons.Count, Is.EqualTo(1));
        Assert.That(buttons[0].Data, Is.EqualTo(CommandRouter.LowPagePrefix + "1"));
    }

    // ---------------- callback data is untrusted ----------------

    [TestCase("low:999")]
    [TestCase("low:-4")]
    public void AStaleOrForgedPageNumber_GivesAValidPage(string data)
    {
        // A button from an older, longer list is normal: the manager left the message in their chat
        // and pressed it an hour later. And the value arrives from a client, so it could be anything.
        _pharmacy.Low = Enumerable.Range(1, 25).Select(Line).ToList();

        Reply? reply = _router.HandlePress(data);

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.Text, Is.Not.Empty);
    }

    [TestCase("low:abc")]
    [TestCase("low:")]
    [TestCase("something-else")]
    [TestCase("")]
    [TestCase(null)]
    public void CallbackDataTheBotDidNotIssue_IsIgnored(string? data)
    {
        // Ignored rather than guessed at. Nothing here can be turned into somebody else's data —
        // the only thing the token encodes is a page number, and the list is re-read for the caller.
        Assert.That(_router.HandlePress(data!), Is.Null);
    }

    [Test]
    public void EveryPagingButtonFitsTelegramsSixtyFourByteLimit()
    {
        _pharmacy.Low = Enumerable.Range(1, 1000).Select(Line).ToList();

        foreach (int page in new[] { 0, 1, 50, 98 })
        {
            Reply? reply = _router.HandlePress(CommandRouter.LowPagePrefix + page);
            foreach (Button b in reply!.Buttons ?? [])
                Assert.That(System.Text.Encoding.UTF8.GetByteCount(b.Data), Is.LessThanOrEqualTo(64),
                    "Telegram refuses callback data over 64 bytes");
        }
    }
}
