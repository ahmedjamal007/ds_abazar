using Dawaii.Core;
using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Telegram;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The owner's assistant: the Arabic menu, and the reports behind it.
///
/// The point of this feature is a pharmacy owner standing somewhere that is not their office,
/// wanting one number. So the tests are about what reaches their phone: that the payment split is
/// the pharmacy's own, that an employee is named rather than user-named, that a shift's end time is
/// not invented, and that nothing here can be reached by somebody who should not reach it.
/// </summary>
[TestFixture]
public class OwnerReportTests
{
    private sealed class NoLinking : ILinkService
    {
        public LinkOutcome Redeem(string code, long telegramUserId, long chatId) => LinkOutcome.NoSuchCode;
    }

    private TestPharmacy _pharmacy = null!;
    private CommandRouter _router = null!;

    private static readonly Sender Owner = new(111222333, 111222333, ErpUserId: 7);

    [SetUp]
    public void SetUp()
    {
        _pharmacy = new TestPharmacy();
        _router = new CommandRouter(new NoLinking(), _pharmacy, "صيدلية النيل");
    }

    private Reply? Press(string data) => _router.HandlePress(data, Owner);
    private Reply? Type(string text) => _router.HandleText(text, Owner);

    private static User Staff(string full, string username) =>
        new() { Id = 3, Username = username, FullName = full, Role = Role.Cashier, IsActive = true };

    private static ShiftTill Till(decimal cash = 0, decimal bankak = 0, decimal fawry = 0, decimal ocash = 0)
        => new() { Cash = cash, Bankak = bankak, Fawry = fawry, Ocash = ocash };

    // ---------------- the menu ----------------

    [Test]
    public void TheWelcomeGreetsTheOwnerByTheirOwnPharmacy()
    {
        Reply? reply = _router.Handle(CommandLine.Parse("/start"), Owner);

        Assert.That(reply!.Text, Does.Contain("صيدلية النيل"),
            "greeting them by the software rather than their shop is a worse first impression");
    }

    [Test]
    public void EveryMenuScreenIsReachableAndHasAWayBack()
    {
        foreach (string screen in new[] { Menu.Reports, Menu.People, Menu.Search })
        {
            Reply? reply = Press(screen);
            Assert.That(reply, Is.Not.Null, screen);
            Assert.That(reply!.Buttons!.Any(b => b.Data == Menu.Main), Is.True,
                screen + " must offer a way home — a phone has no back button");
        }
    }

    [Test]
    public void EveryReportScreenOffersAWayBack()
    {
        foreach (string action in new[]
                 { Menu.SalesToday, Menu.SalesByEmployee, Menu.Shifts, Menu.Purchases,
                   Menu.Expiring, Menu.CustomerDebt, Menu.SupplierDebt, Menu.Customers, Menu.Suppliers })
        {
            Reply? reply = Press(action);
            Assert.That(reply, Is.Not.Null, action);
            Assert.That(reply!.Buttons, Is.Not.Null.And.Not.Empty, action + " must not be a dead end");
        }
    }

    [Test]
    public void EveryCallbackTokenFitsTelegramsSixtyFourByteLimit()
    {
        // Arabic is two to three bytes a character, so a token carrying a readable label would breach
        // the limit silently and the button would simply not work.
        foreach (string screen in new[] { Menu.Main, Menu.Reports, Menu.People, Menu.Search })
        foreach (Button b in Press(screen)!.Buttons ?? [])
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(b.Data), Is.LessThanOrEqualTo(64), b.Label);
    }

    [Test]
    public void ATokenTheBotDidNotIssue_IsIgnored()
    {
        Assert.That(Press("m:nonsense"), Is.Null);
        Assert.That(Press("r:nonsense"), Is.Null);
        Assert.That(Press("something-else"), Is.Null);
    }

    // ---------------- sales, split the way the money arrived ----------------

    [Test]
    public void TodaysSalesBreakDownByPaymentMethod()
    {
        _pharmacy.TakingsLine = new TakingsLine(
            Till(cash: 1000m, bankak: 500m, fawry: 250m, ocash: 125m),
            CreditTotal: 300m, InvoiceCount: 14, ReturnedCount: 0, ReturnedTotal: 0m,
            ProfitVisible: false, Profit: 0m);

        string text = Press(Menu.SalesToday)!.Text;

        Assert.That(text, Does.Contain("نقدي"));
        Assert.That(text, Does.Contain("بنكك"));
        Assert.That(text, Does.Contain("فوري"));
        Assert.That(text, Does.Contain("أوكاش"));
        Assert.That(text, Does.Contain("آجل"));
        Assert.That(text, Does.Contain("2,175.00"), "and they add up, credit included");
    }

    [Test]
    public void AChannelWithNothingInIt_StillShowsAsZero()
    {
        // A missing line reads as missing data. A zero reads as "none today", which is the answer.
        _pharmacy.TakingsLine = new TakingsLine(
            Till(cash: 100m), 0m, 1, 0, 0m, false, 0m);

        string text = Press(Menu.SalesToday)!.Text;

        Assert.That(text, Does.Contain("بنكك: 0.00"));
        Assert.That(text, Does.Contain("أوكاش: 0.00"));
    }

    [Test]
    public void ProfitAppearsOnlyWhenTheErpAllowsIt()
    {
        _pharmacy.TakingsLine = new TakingsLine(Till(cash: 100m), 0m, 1, 0, 0m, false, 40m);
        Assert.That(Press(Menu.SalesToday)!.Text, Does.Not.Contain("الأرباح"));

        _pharmacy.TakingsLine = new TakingsLine(Till(cash: 100m), 0m, 1, 0, 0m, true, 40m);
        Assert.That(Press(Menu.SalesToday)!.Text, Does.Contain("الأرباح"));
    }

    [Test]
    public void ADayWithNoSales_SaysSoRatherThanShowingFiveZeroes()
    {
        _pharmacy.TakingsLine = new TakingsLine(Till(), 0m, 0, 0, 0m, false, 0m);

        Assert.That(Press(Menu.SalesToday)!.Text, Does.Contain("لا توجد مبيعات"));
    }

    // ---------------- by employee, named properly ----------------

    [Test]
    public void EmployeeSalesUseTheFullName_NotTheLoginName()
    {
        _pharmacy.ByEmployee =
        [
            new EmployeeTakings(Staff("علاء الأمين", "alaa01"),
                new TakingsLine(Till(cash: 800m), 0m, 6, 0, 0m, false, 0m))
        ];

        string text = Press(Menu.SalesByEmployee)!.Text;

        Assert.That(text, Does.Contain("علاء الأمين"));
        Assert.That(text, Does.Not.Contain("alaa01"),
            "an owner reads a report about a person, not about a login");
    }

    [Test]
    public void AnEmployeeWithNoFullName_FallsBackToTheirUsername()
    {
        // Better than an empty line: the owner can still tell which account it was.
        _pharmacy.ByEmployee =
        [
            new EmployeeTakings(Staff("", "alaa01"),
                new TakingsLine(Till(cash: 100m), 0m, 1, 0, 0m, false, 0m))
        ];

        Assert.That(Press(Menu.SalesByEmployee)!.Text, Does.Contain("alaa01"));
    }

    [Test]
    public void EachEmployeeGetsTheSameBreakdownAsThePharmacy()
    {
        _pharmacy.ByEmployee =
        [
            new EmployeeTakings(Staff("علاء", "a"),
                new TakingsLine(Till(cash: 500m, bankak: 200m), 100m, 5, 0, 0m, false, 0m))
        ];

        string text = Press(Menu.SalesByEmployee)!.Text;

        foreach (string channel in new[] { "نقدي", "بنكك", "فوري", "أوكاش", "آجل" })
            Assert.That(text, Does.Contain(channel), channel);
        Assert.That(text, Does.Contain("800.00"), "their own total");
    }

    // ---------------- shifts, without inventing an end time ----------------

    [Test]
    public void AShiftShowsSignInAndLastTransaction_NeverALogout()
    {
        // The schema records logins and no logouts at all. An owner deciding whether somebody left
        // early must be told they are looking at when the tills stopped, not at a clock-out.
        _pharmacy.ShiftRows =
        [
            new ShiftLine(Staff("علاء الأمين", "alaa01"), new DateTime(2026, 9, 26),
                new DateTime(2026, 9, 26, 8, 15, 0),
                new DateTime(2026, 9, 26, 20, 40, 0),
                Till(cash: 1200m), 18, 0m)
        ];

        string text = Press(Menu.Shifts)!.Text;

        Assert.That(text, Does.Contain("علاء الأمين"));
        Assert.That(text, Does.Not.Contain("alaa01"), "named here too, not user-named");
        Assert.That(text, Does.Contain("08:15"));
        Assert.That(text, Does.Contain("20:40"));
        Assert.That(text, Does.Contain("آخر عملية"), "labelled as a last transaction");
        Assert.That(text, Does.Contain("غير مسجّل"), "and the report says the end time is not recorded");
    }

    [Test]
    public void AnEmployeeWhoSignedInButSoldNothing_ShowsADashNotAFakeTime()
    {
        _pharmacy.ShiftRows =
        [
            new ShiftLine(Staff("سارة", "sara"), new DateTime(2026, 9, 26),
                new DateTime(2026, 9, 26, 9, 0, 0), null, Till(), 0, 0m)
        ];

        Assert.That(Press(Menu.Shifts)!.Text, Does.Contain("—"));
    }

    // ---------------- customers and suppliers ----------------

    [Test]
    public void CustomerDebtsLeadWithTheTotal()
    {
        _pharmacy.Debtors =
        [
            new Customer { Name = "أحمد", Phone = "0912345678", Balance = 1500m },
            new Customer { Name = "سارة", Balance = 400m },
        ];
        _pharmacy.CustomerTotal = 1900m;

        string text = Press(Menu.CustomerDebt)!.Text;

        Assert.That(text, Does.Contain("1,900.00"));
        Assert.That(text, Does.Contain("أحمد"));
        Assert.That(text, Does.Contain("0912345678"), "with the phone, so the owner can call them");
    }

    [Test]
    public void SupplierDebtsSayWhatThePharmacyOwes()
    {
        _pharmacy.SupplierRows =
        [
            new Supplier { Name = "شركة النيل", Outstanding = 52000m, InvoiceCount = 3 }
        ];
        _pharmacy.SupplierTotal = 52000m;

        string text = Press(Menu.SupplierDebt)!.Text;

        Assert.That(text, Does.Contain("52,000.00"));
        Assert.That(text, Does.Contain("شركة النيل"));
    }

    [Test]
    public void NothingOwed_IsGoodNewsStatedPlainly()
    {
        Assert.That(Press(Menu.CustomerDebt)!.Text, Does.Contain("لا يوجد"));
        Assert.That(Press(Menu.SupplierDebt)!.Text, Does.Contain("لا توجد"));
    }

    // ---------------- purchases, with who entered them ----------------

    [Test]
    public void APurchaseShowsTheCompanyRepresentativeAndWhoEnteredIt()
    {
        _pharmacy.PurchaseRows =
        [
            new PurchaseLine(new PurchaseInvoice
            {
                Id = 1, InvoiceNumber = "INV-42", SupplierName = "شركة النيل",
                Representative = "المندوب علي", InvoiceDate = new DateTime(2026, 9, 20),
                Total = 52000m, UserName = "علاء الأمين",
                CreatedAt = new DateTime(2026, 9, 20, 14, 30, 0)
            })
        ];

        string text = Press(Menu.Purchases)!.Text;

        Assert.That(text, Does.Contain("INV-42"));
        Assert.That(text, Does.Contain("شركة النيل"));
        Assert.That(text, Does.Contain("المندوب علي"));
        Assert.That(text, Does.Contain("52,000.00"));
        Assert.That(text, Does.Contain("علاء الأمين"), "who entered it");
        Assert.That(text, Does.Contain("14:30"), "and when");
    }

    // ---------------- expiry ----------------

    [Test]
    public void ExpiringStockLeadsWithWhatIsAlreadyLost()
    {
        var item = new Item { NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10 };
        _pharmacy.ExpiringRows =
        [
            new NearExpiryRow
            {
                Item = item, DaysUntilExpiry = -5,
                Batch = new StockBatch
                {
                    QuantityUnits = 200, UnitsPerStrip = 10, StripsPerBox = 10,
                    ExpiryDate = DateTime.Today.AddDays(-5), BatchNumber = "A1", BoxPurchasePrice = 1000m
                }
            }
        ];

        string text = Press(Menu.Expiring)!.Text;

        Assert.That(text, Does.Contain("منتهية"));
        Assert.That(text, Does.Contain("panadol"));
        Assert.That(text, Does.Contain("A1"), "the batch, so it can be found on the shelf");
        Assert.That(text, Does.Contain("2 علبة"), "in boxes, not 200 tablets");
    }

    // ---------------- typing a name or a barcode ----------------

    [Test]
    public void TypingADrugName_AnswersWithItsPrice()
    {
        var item = new Item
        {
            NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 60m
        };
        _pharmacy.PriceRows = [new PriceLine(item, 1200)];

        string text = Type("panadol")!.Text;

        Assert.That(_pharmacy.LastPriceTerm, Is.EqualTo("panadol"));
        Assert.That(text, Does.Contain("6,000.00"), "a box is 100 units at 60");
        Assert.That(text, Does.Contain("12 علبة"), "and what is on the shelf");
    }

    [Test]
    public void ADrugWithNoPriceSaysSo_RatherThanShowingZero()
    {
        _pharmacy.PriceRows = [new PriceLine(new Item { NameEn = "new-drug" }, 100)];

        Assert.That(Type("new")!.Text, Does.Contain("لم يُحدَّد سعر"));
    }

    [Test]
    public void SeveralMatches_AreListedSoTheOwnerCanNarrowIt()
    {
        _pharmacy.PriceRows =
        [
            new PriceLine(new Item { NameEn = "panadol", SellingPrice = 60m }, 100),
            new PriceLine(new Item { NameEn = "panadol extra", SellingPrice = 80m }, 50),
        ];

        Assert.That(Type("pana")!.Text, Does.Contain("panadol extra"));
    }

    [Test]
    public void NothingMatching_SuggestsWhatToDoNext()
    {
        _pharmacy.PriceRows = [];

        string text = Type("zzzz")!.Text;
        Assert.That(text, Does.Contain("zzzz"));
        Assert.That(text, Does.Contain("*"), "including the shortcut for the whole list");
    }

    [Test]
    public void AsteriskSendsTheWholePriceListAsAFile()
    {
        // Hundreds of drugs cannot fit in a message, and Telegram refuses rather than truncates.
        _pharmacy.AllPriceRows = Enumerable.Range(1, 400)
            .Select(i => new PriceLine(
                new Item { NameEn = "drug-" + i, UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = i },
                100))
            .ToList();

        Reply? reply = Type("*");

        Assert.That(reply!.File, Is.Not.Null);
        Assert.That(reply.File!.Name, Does.EndWith(".csv"));
        Assert.That(reply.Text, Does.Contain("400"));
        Assert.That(reply.Text.Length, Is.LessThan(CommandRouter.TelegramTextLimit));
    }

    [Test]
    public void ASingleCharacter_IsNotTreatedAsASearch()
    {
        // Matching on one letter returns half the catalogue and looks broken.
        Assert.That(Type("p"), Is.Null);
        Assert.That(Type(" "), Is.Null);
    }

    // ---------------- permissions still come from the ERP ----------------

    [Test]
    public void EveryReportRunsAsTheLinkedPharmacyAccount()
    {
        Press(Menu.SalesToday);
        Assert.That(_pharmacy.ErpUserIdSeen, Is.EqualTo(7));

        _pharmacy.ErpUserIdSeen = 0;
        Press(Menu.Purchases);
        Assert.That(_pharmacy.ErpUserIdSeen, Is.EqualTo(7));
    }

    [Test]
    public void WhenTheErpRefuses_ItsWordingIsShownAndTheMenuStillWorks()
    {
        _pharmacy.Refuse = new PermissionDeniedException("التقارير متاحة للمدير فقط.");

        Reply? reply = Press(Menu.SalesToday);

        Assert.That(reply!.Text, Is.EqualTo("التقارير متاحة للمدير فقط."));
        Assert.That(reply.Buttons, Is.Not.Null.And.Not.Empty,
            "a refusal must not strand the owner on a screen with no way out");
    }

    // ---------------- the message limit ----------------

    [Test]
    public void EveryMenuReportFitsInOneTelegramMessage()
    {
        _pharmacy.Debtors = Enumerable.Range(1, 500)
            .Select(i => new Customer { Name = "عميل رقم " + i, Phone = "09123456" + i, Balance = i })
            .ToList();
        _pharmacy.SupplierRows = Enumerable.Range(1, 500)
            .Select(i => new Supplier { Name = "شركة رقم " + i, Outstanding = i, InvoiceCount = i })
            .ToList();
        _pharmacy.ByEmployee = Enumerable.Range(1, 100)
            .Select(i => new EmployeeTakings(Staff("موظف رقم " + i, "u" + i),
                new TakingsLine(Till(cash: i), i, i, 0, 0m, false, 0m)))
            .ToList();

        foreach (string action in new[]
                 { Menu.CustomerDebt, Menu.SupplierDebt, Menu.Customers, Menu.Suppliers,
                   Menu.SalesByEmployee })
            Assert.That(Press(action)!.Text.Length,
                Is.LessThan(CommandRouter.TelegramTextLimit), action);
    }
}
