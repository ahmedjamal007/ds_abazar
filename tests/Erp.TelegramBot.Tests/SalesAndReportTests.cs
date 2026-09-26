using System.Text;
using Dawaii.Core;
using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// /sales and /report.
///
/// Two things here are easy to get wrong and expensive when wrong. The PERIOD, because "week" and
/// "month" are ambiguous words and the answer is money — a manager acting on a figure that measures
/// two days instead of thirty is a real cost. And the FILE, because a CSV of Arabic drug names
/// without a byte-order mark opens in Excel as mojibake: perfectly valid, and indistinguishable from
/// corruption to the person who asked for it.
/// </summary>
[TestFixture]
public class SalesAndReportTests
{
    private sealed class NoLinking : ILinkService
    {
        public LinkOutcome Redeem(string code, long telegramUserId, long chatId) => LinkOutcome.NoSuchCode;
    }

    private TestPharmacy _pharmacy = null!;
    private CommandRouter _router = null!;

    /// <summary>Telegram id, chat id, and the linked pharmacy account.</summary>
    private static readonly Sender Manager = new(111222333, 111222333, ErpUserId: 7);

    private static readonly DateTime Noon = new(2026, 9, 26, 12, 0, 0);

    [SetUp]
    public void SetUp()
    {
        _pharmacy = new TestPharmacy();
        _router = new CommandRouter(new NoLinking(), _pharmacy);
    }

    private Reply? Route(string text) => _router.Handle(CommandLine.Parse(text), Manager);

    // ---------------- periods ----------------

    [Test]
    public void Today_IsJustToday()
    {
        Assert.That(Period.TryParse("today", Noon, out Period p), Is.True);

        Assert.That(p.FromInclusive, Is.EqualTo(new DateTime(2026, 9, 26)));
        Assert.That(p.ToExclusive, Is.EqualTo(new DateTime(2026, 9, 27)));
    }

    [Test]
    public void Week_IsSevenDaysIncludingToday()
    {
        Assert.That(Period.TryParse("week", Noon, out Period p), Is.True);

        Assert.That((p.ToExclusive - p.FromInclusive).Days, Is.EqualTo(7));
        Assert.That(p.FromInclusive, Is.EqualTo(new DateTime(2026, 9, 20)));
    }

    [Test]
    public void Month_IsThirtyDaysIncludingToday()
    {
        Assert.That(Period.TryParse("month", Noon, out Period p), Is.True);
        Assert.That((p.ToExclusive - p.FromInclusive).Days, Is.EqualTo(30));
    }

    [Test]
    public void EveryPeriod_CarriesALabelSayingWhatItMeasured()
    {
        // The reason these are rolling windows at all. "this week" on a Sunday could mean one day or
        // seven depending on where you are; a manager must never have to guess which they got.
        foreach (string word in new[] { "today", "week", "month" })
        {
            Period.TryParse(word, Noon, out Period p);
            Assert.That(p.Label, Is.Not.Empty, word);
        }

        Period.TryParse("week", Noon, out Period week);
        Assert.That(week.Label, Does.Contain("7"), "it says seven days, not 'this week'");
    }

    [TestCase("اليوم")]
    [TestCase("اسبوع")]
    [TestCase("شهر")]
    public void TheArabicWords_AreAcceptedToo(string word)
    {
        // A manager on an Arabic keyboard reaches for these first.
        Assert.That(Period.TryParse(word, Noon, out _), Is.True);
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("year")]
    [TestCase("yesterday")]
    [TestCase("42")]
    public void AnUnknownPeriod_IsRefused_RatherThanGuessed(string? word)
    {
        Assert.That(Period.TryParse(word, Noon, out _), Is.False,
            "guessing a period would mean answering a question about money that nobody asked");
    }

    // ---------------- /sales ----------------

    [Test]
    public void SalesWithNoPeriod_ExplainsTheChoices()
    {
        string text = Route("/sales")!.Text;

        Assert.That(text, Does.Contain("today"));
        Assert.That(text, Does.Contain("week"));
        Assert.That(text, Does.Contain("month"));
    }

    [Test]
    public void SalesStatesTheWindowItMeasured()
    {
        _pharmacy.Totals = new DailyReport { TransactionCount = 12, TotalSales = 4500m };

        string text = Route("/sales week")!.Text;

        Assert.That(text, Does.Contain("آخر 7 أيام"));
        Assert.That(text, Does.Contain("2026-09-20"), "and the actual dates");
    }

    [Test]
    public void SalesReportsTheFigures()
    {
        _pharmacy.Totals = new DailyReport
        {
            TransactionCount = 12, TotalSales = 4500m, CashTotal = 3000m, CreditTotal = 1500m,
            ReturnedCount = 2, ReturnedTotal = 300m
        };

        string text = Route("/sales today")!.Text;

        Assert.That(text, Does.Contain("12"));
        Assert.That(text, Does.Contain("4,500"));
        Assert.That(text, Does.Contain("مرتجعات"));
    }

    [Test]
    public void ProfitIsShownOnlyWhenTheErpSaysSo()
    {
        // The ERP decides, from the real account behind the link. The bot does not second-guess it.
        _pharmacy.Totals = new DailyReport
        {
            TransactionCount = 1, TotalSales = 100m, ProfitVisible = false, TotalProfit = 40m
        };
        Assert.That(Route("/sales today")!.Text, Does.Not.Contain("الأرباح"));

        _pharmacy.Totals.ProfitVisible = true;
        Assert.That(Route("/sales today")!.Text, Does.Contain("الأرباح"));
    }

    [Test]
    public void NoSales_SaysSoRatherThanShowingZeroes()
    {
        _pharmacy.Totals = new DailyReport { TransactionCount = 0 };

        Assert.That(Route("/sales today")!.Text, Does.Contain("لا مبيعات"));
    }

    [Test]
    public void TheLinkedErpAccount_IsWhatTheQueryRunsAs()
    {
        Route("/sales today");

        Assert.That(_pharmacy.ErpUserIdSeen, Is.EqualTo(7),
            "the pharmacy's own services apply this account's permissions");
    }

    [Test]
    public void WhenTheErpRefuses_ItsOwnWordingIsPassedOn()
    {
        // A manager demoted since linking. The ERP's message is the truthful one; replacing it with
        // something vaguer would leave them guessing why their bot stopped answering.
        _pharmacy.Refuse = new PermissionDeniedException("التقارير متاحة للمدير فقط.");

        Assert.That(Route("/sales today")!.Text, Is.EqualTo("التقارير متاحة للمدير فقط."));
    }

    // ---------------- /report ----------------

    [Test]
    public void ReportWithNoType_ListsTheTypes()
    {
        string text = Route("/report")!.Text;

        foreach (string type in new[] { "sales", "low", "best", "dead" })
            Assert.That(text, Does.Contain(type));
    }

    [Test]
    public void ReportSales_SendsACsvFile()
    {
        _pharmacy.Totals = new DailyReport { TransactionCount = 2, TotalSales = 500m };
        _pharmacy.InvoiceList =
        [
            new Sale { SaleNumber = 1, Total = 300m, CreatedAt = Noon, SaleType = SaleType.Cash,
                       PaymentMethod = PaymentMethods.Cash, Status = SaleStatus.Completed },
            new Sale { SaleNumber = 2, Total = 200m, CreatedAt = Noon, SaleType = SaleType.Credit,
                       Status = SaleStatus.Completed },
        ];

        Reply? reply = Route("/report sales week");

        Assert.That(reply!.File, Is.Not.Null);
        Assert.That(reply.File!.Name, Does.EndWith(".csv"));
        Assert.That(reply.File.Name, Does.Contain("2026-09-20"), "the file names its own period");
        Assert.That(reply.Text, Does.Contain("آخر 7 أيام"), "and the caption says what it covers");
    }

    [Test]
    public void TheCsvStartsWithAByteOrderMark()
    {
        // Without it Excel reads a UTF-8 file as the system code page and every Arabic drug name
        // becomes mojibake — a perfectly valid file that looks like corruption to whoever opened it.
        _pharmacy.Low = [new LowStockLine(new Item { NameEn = "بنادول", MinQuantity = 100 }, 10)];

        byte[] bytes = Route("/report low")!.File!.Bytes;

        Assert.That(bytes[0], Is.EqualTo(0xEF));
        Assert.That(bytes[1], Is.EqualTo(0xBB));
        Assert.That(bytes[2], Is.EqualTo(0xBF));
    }

    [Test]
    public void TheCsvHoldsTheArabicNamesIntact()
    {
        _pharmacy.Low = [new LowStockLine(
            new Item { NameEn = "panadol", GenericName = "باراسيتامول", MinQuantity = 100 }, 10)];

        string csv = Encoding.UTF8.GetString(Route("/report low")!.File!.Bytes);

        Assert.That(csv, Does.Contain("panadol"));
        Assert.That(csv, Does.Contain("باراسيتامول"));
    }

    [Test]
    public void AValueExcelWouldTreatAsAFormula_IsNeutralised()
    {
        // A drug name is not a formula, and a file forwarded to an accountant must not be able to run
        // one. Excel and LibreOffice both execute a cell beginning with = + - or @.
        _pharmacy.Low = [new LowStockLine(
            new Item { NameEn = "=cmd|' /c calc'!A1", MinQuantity = 100 }, 10)];

        string csv = Encoding.UTF8.GetString(Route("/report low")!.File!.Bytes);

        Assert.That(csv, Does.Not.Contain("\n=cmd"));
        Assert.That(csv, Does.Contain("'=cmd").Or.Contain("\"'=cmd"),
            "prefixed so it stays text");
    }

    [Test]
    public void ACommaInANameDoesNotBreakTheColumns()
    {
        _pharmacy.Low = [new LowStockLine(
            new Item { NameEn = "panadol, extra", MinQuantity = 100 }, 10)];

        string csv = Encoding.UTF8.GetString(Route("/report low")!.File!.Bytes);

        Assert.That(csv, Does.Contain("\"panadol, extra\""), "quoted, or every column after it shifts");
    }

    [Test]
    public void ReportLow_WithNothingLow_SaysSoRatherThanSendingAnEmptyFile()
    {
        _pharmacy.Low = [];

        Reply? reply = Route("/report low");

        Assert.That(reply!.File, Is.Null, "an empty spreadsheet answers nothing");
        Assert.That(reply.Text, Does.Contain("لا يوجد"));
    }

    [Test]
    public void ReportBest_AndReportDead_BothProduceFiles()
    {
        _pharmacy.Best = [new BestSellerRow { Name = "panadol", UnitsSold = 400, Revenue = 2400m }];
        _pharmacy.Dead =
        [
            new DeadStockRow
            {
                Item = new Item { NameEn = "old-drug", PurchasePrice = 5m }, AvailableUnits = 60
            }
        ];

        Assert.That(Route("/report best month")!.File, Is.Not.Null);

        Reply? dead = Route("/report dead");
        Assert.That(dead!.File, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(dead.File!.Bytes), Does.Contain("300.00"),
            "60 units at a cost of 5 — money already spent and sitting on a shelf");
    }

    [Test]
    public void AnUnknownReportType_ListsTheTypes()
    {
        Assert.That(Route("/report nonsense")!.Text, Does.Contain("sales"));
    }

    [Test]
    public void AReportTheErpRefuses_PassesTheRefusalOn()
    {
        _pharmacy.Refuse = new PermissionDeniedException("التقارير متاحة للمدير فقط.");

        Reply? reply = Route("/report best month");

        Assert.That(reply!.File, Is.Null, "no file leaks out of a refused request");
        Assert.That(reply.Text, Is.EqualTo("التقارير متاحة للمدير فقط."));
    }

    [Test]
    public void EveryTextReply_FitsInOneTelegramMessage()
    {
        _pharmacy.Totals = new DailyReport { TransactionCount = 5, TotalSales = 100m };

        foreach (string text in new[]
                 { "/sales", "/sales today", "/report", "/report nonsense", "/report low" })
            Assert.That(Route(text)!.Text.Length, Is.LessThan(CommandRouter.TelegramTextLimit), text);
    }
}
