using Dawaii.Core;
using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using Erp.TelegramBot.Telegram;

namespace Erp.TelegramBot.Commands;

/// <summary>Who sent a command, as much as any handler needs to know.</summary>
/// <param name="TelegramUserId">The numeric Telegram account. What authorization is decided on.</param>
/// <param name="ChatId">Where to reply. Not the same as the sender in a group.</param>
/// <param name="ErpUserId">
/// The pharmacy account this Telegram id is linked to, or 0 when it is not linked. Handed to the
/// ERP's own report services so THEY decide what this person may see — the bot does not
/// re-implement any of those rules and so cannot get them subtly wrong.
/// </param>
public readonly record struct Sender(long TelegramUserId, long ChatId, int ErpUserId = 0);

/// <summary>What to say back: text with optional buttons, or a file.</summary>
public sealed record Reply(
    string Text, IReadOnlyList<Button>? Buttons = null, OutgoingFile? File = null)
{
    public static implicit operator Reply(string text) => new(text);

    /// <summary>A file with a one-line caption.</summary>
    public static Reply Document(OutgoingFile file, string caption) => new(caption, null, file);
}

/// <summary>
/// Turns a parsed command into the text to send back.
///
/// Returns a value rather than sending one, so every command's wording — which is the bot's entire
/// product — can be asserted in a test with no token and no network. Null means "say nothing", which
/// is a real answer rather than a failure.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>
    /// Telegram refuses a message over 4096 characters. Anything that can grow with the pharmacy's
    /// data has to page or be sent as a document; the number lives next to the thing that respects it.
    /// </summary>
    public const int TelegramTextLimit = 4096;

    /// <summary>Prefix on the callback data behind the /low paging buttons.</summary>
    public const string LowPagePrefix = "low:";

    private readonly ILinkService _linking;
    private readonly IPharmacyReader _pharmacy;
    private readonly string _pharmacyName;

    public CommandRouter(ILinkService linking, IPharmacyReader pharmacy, string? pharmacyName = null)
    {
        _linking = linking;
        _pharmacy = pharmacy;
        _pharmacyName = string.IsNullOrWhiteSpace(pharmacyName) ? "دوائي" : pharmacyName.Trim();
    }

    /// <summary>
    /// The commands an UNLINKED sender may use. Everything else from someone the bot does not know
    /// gets silence, so a stranger cannot even establish that the bot is real.
    ///
    /// /link has to be here: it is how a manager becomes known in the first place.
    /// </summary>
    public static bool IsOpenToStrangers(string verb) => verb == "link";

    /// <summary>
    /// A message that is NOT a command.
    ///
    /// For a linked owner this is the fastest path in the whole bot: type a drug name or scan a
    /// barcode straight into the chat and get the price back, with no command to remember. "*" asks
    /// for the whole price list, which has to be a file — hundreds of drugs will not fit in a message.
    ///
    /// Returns null for anybody the bot does not know, so a stranger messaging it still gets silence.
    /// </summary>
    public Reply? HandleText(string? text, Sender sender)
    {
        string term = (text ?? "").Trim();
        if (term.Length == 0) return null;

        if (term == "*") return FullPriceList();

        // Two characters is not a search, it is a typo — and matching on it would return half the
        // catalogue and look broken.
        if (term.Length < 2) return null;

        IReadOnlyList<PriceLine> matches = _pharmacy.Prices(term, PharmacyReader.SearchLimit);

        if (matches.Count == 0) return new Reply(OwnerReports.PriceNotFound(term), Menu.BackTo(Menu.Search));
        if (matches.Count == 1) return new Reply(OwnerReports.Price(matches[0]), Menu.BackTo(Menu.Search));

        return new Reply(OwnerReports.PriceMatches(term, matches, ListPreview), Menu.BackTo(Menu.Search));
    }

    public Reply? Handle(CommandLine command, Sender sender)
    {
        if (!command.IsCommand) return null;      // ordinary chatter is not an error

        return command.Verb switch
        {
            "start" => Menu.Welcome(_pharmacyName),
            "menu" => Menu.MainMenu(),
            "help" => Menu.Help(),
            "ping" => "pong",
            "link" => Link(command, sender),
            "stock" => Stock(command),
            "low" => Low(0),
            "sales" => Sales(command, sender),
            "report" => Report(command, sender),
            _ => "أمر غير معروف: /" + command.Verb + "\nاكتب /help لعرض الأوامر.",
        };
    }

    /// <summary>
    /// A paging button press.
    ///
    /// The data arrives from a client and is therefore UNTRUSTED: a tampered or stale value must
    /// produce a sane page rather than an exception or somebody else's data. Since the only thing it
    /// encodes is a page number, and the list is re-read fresh for the caller, the worst a forged
    /// value can do is show a page of the same manager's own low-stock list.
    /// </summary>
    public Reply? HandlePress(string callbackData, Sender sender = default)
    {
        if (callbackData is null) return null;

        if (callbackData.StartsWith(LowPagePrefix, StringComparison.Ordinal))
        {
            string raw = callbackData[LowPagePrefix.Length..];
            return int.TryParse(raw, out int page) ? Low(page) : null;
        }

        if (!Menu.Owns(callbackData)) return null;

        try
        {
            return callbackData switch
            {
                Menu.Main => Menu.MainMenu(),
                Menu.Reports => Menu.ReportsMenu(),
                Menu.People => Menu.PeopleMenu(),
                Menu.Search => Menu.SearchMenu(),
                "m:help" => Menu.Help(),

                Menu.SalesToday => Screen(Menu.Reports,
                    OwnerReports.Takings(_pharmacy.Takings(sender.ErpUserId, Today), Today)),

                Menu.SalesByEmployee => Screen(Menu.Reports,
                    OwnerReports.ByEmployee(_pharmacy.TakingsByEmployee(sender.ErpUserId, Today), Today, ListPreview)),

                Menu.Shifts => Screen(Menu.Reports,
                    OwnerReports.Shifts(_pharmacy.Shifts(sender.ErpUserId, DateTime.Today), DateTime.Today, ListPreview)),

                Menu.Purchases => Screen(Menu.Reports, OwnerReports.Purchases(
                    _pharmacy.Purchases(sender.ErpUserId, ThisMonth), ThisMonth, ListPreview)),

                Menu.Expiring => Screen(Menu.Reports,
                    OwnerReports.Expiring(_pharmacy.Expiring(), ListPreview)),

                Menu.LowStock => Low(0),

                Menu.CustomerDebt => Screen(Menu.People, OwnerReports.CustomerDebts(
                    _pharmacy.CustomersInDebt(), _pharmacy.CustomerDebtTotal(), ListPreview)),

                Menu.SupplierDebt => Screen(Menu.People, OwnerReports.SupplierDebts(
                    _pharmacy.SuppliersOwed(), _pharmacy.SupplierDebtTotal(), ListPreview)),

                Menu.Customers => Screen(Menu.People,
                    OwnerReports.Customers(_pharmacy.FindCustomers(""), ListPreview)),

                Menu.Suppliers => Screen(Menu.People,
                    OwnerReports.Suppliers(_pharmacy.SuppliersOwed(), ListPreview)),

                Menu.PriceList => FullPriceList(),

                _ => null,
            };
        }
        catch (DomainException ex)
        {
            // The ERP refused — a demoted account, most likely. Its wording is the truthful one.
            return new Reply(ex.Message, Menu.BackTo(Menu.Main));
        }
    }

    /// <summary>A report with a way back under it. Every screen has one; a phone has no back button.</summary>
    private static Reply Screen(string parent, string body) => new(body, Menu.BackTo(parent));

    /// <summary>
    /// The whole price list.
    ///
    /// A file, not a message: a pharmacy carries hundreds of drugs and Telegram refuses anything over
    /// 4096 characters. The count goes in the caption so the owner sees an answer immediately rather
    /// than only a download.
    /// </summary>
    private Reply FullPriceList()
    {
        IReadOnlyList<PriceLine> all = _pharmacy.AllPrices();
        if (all.Count == 0)
            return new Reply("لا توجد أصناف مسعّرة.", Menu.BackTo(Menu.Search));

        return new Reply(
            "💰 قائمة الأسعار — " + all.Count + " صنف",
            Menu.BackTo(Menu.Search),
            PriceListCsv(all));
    }

    private static OutgoingFile PriceListCsv(IReadOnlyList<PriceLine> all)
    {
        var section = new CsvSection
        {
            Heading = "قائمة الأسعار",
            Columns =
            [
                "الصنف", "الاسم العلمي",
                "سعر العلبة", "سعر الشريط",
                "سعر الحبة", "المتوفر (حبة)"
            ]
        };

        foreach (PriceLine p in all)
            section.Add(
                p.Item.DisplayName,
                p.Item.GenericName ?? "",
                p.Unpriced ? "" : ReportFile.Money(p.BoxPrice),
                p.Unpriced ? "" : ReportFile.Money(p.StripPrice),
                p.Unpriced ? "" : ReportFile.Money(p.UnitPrice),
                ReportFile.Int(p.AvailableUnits));

        return ReportFile.Csv("prices_" + ReportFile.Date(DateTime.Now) + ".csv",
            "قائمة أسعار الأصناف", [section]);
    }

    /// <summary>How many rows a menu report shows before saying "and N others".</summary>
    private const int ListPreview = 15;

    private static Period Today
    {
        get { Period.TryParse("today", DateTime.Now, out Period p); return p; }
    }

    private static Period ThisMonth
    {
        get { Period.TryParse("month", DateTime.Now, out Period p); return p; }
    }

    // ---------------- /stock ----------------

    private Reply Stock(CommandLine command)
    {
        string query = command.Rest.Trim();
        if (query.Length == 0) return StockReport.Usage;

        StockDetail? one = _pharmacy.FindOne(query);
        if (one != null) return StockReport.One(one);

        // Nothing matched exactly. Say which it was — nothing at all, or too many — because the
        // manager's next action differs: scan the barcode, or type more of the name.
        IReadOnlyList<Item> many = _pharmacy.FindMany(query, PharmacyReader.SearchLimit);
        if (many.Count == 0) return StockReport.NotFound(query);

        return StockReport.Ambiguous(query, many);
    }

    // ---------------- /low ----------------

    private Reply Low(int pageIndex)
    {
        IReadOnlyList<LowStockLine> all = _pharmacy.LowStock();
        LowStockPage page = LowStockReport.Page(all, pageIndex);

        return new Reply(page.Text, PageButtons(page));
    }

    /// <summary>
    /// Previous/next, and only when there is somewhere to go. A disabled-looking button that does
    /// nothing is worse than no button: on a phone it reads as the bot having stopped responding.
    /// </summary>
    private static IReadOnlyList<Button>? PageButtons(LowStockPage page)
    {
        if (page.PageCount <= 1) return null;

        var buttons = new List<Button>();
        if (page.HasPrevious)
            buttons.Add(new Button("◀ السابق", LowPagePrefix + (page.PageIndex - 1)));
        if (page.HasNext)
            buttons.Add(new Button("التالي ▶", LowPagePrefix + (page.PageIndex + 1)));

        return buttons.Count > 0 ? buttons : null;
    }

    // ---------------- /sales ----------------

    private Reply Sales(CommandLine command, Sender sender)
    {
        if (!Period.TryParse(command.FirstArg, DateTime.Now, out Period period))
            return SalesReport.Usage;

        try
        {
            return SalesReport.Summary(_pharmacy.Sales(sender.ErpUserId, period), period);
        }
        catch (DomainException ex)
        {
            // The ERP refused — a demoted account, most likely. Its message is the honest one.
            return ex.Message;
        }
    }

    // ---------------- /report ----------------

    /// <summary>
    /// Builds a report as a file.
    ///
    /// A file rather than a message because these outgrow 4096 characters immediately, and because a
    /// CSV is the thing a manager can actually forward to an accountant. Built in memory and sent
    /// straight from it.
    /// </summary>
    private Reply Report(CommandLine command, Sender sender)
    {
        string type = (command.FirstArg ?? "").Trim().ToLowerInvariant();
        if (type.Length == 0) return ReportUsage;

        try
        {
            switch (type)
            {
                case "sales":
                {
                    if (!Period.TryParse(command.Args.Length > 1 ? command.Args[1] : "today",
                            DateTime.Now, out Period period))
                        return ReportUsage;

                    DailyReport totals = _pharmacy.Sales(sender.ErpUserId, period);
                    IReadOnlyList<Sale> invoices = _pharmacy.Invoices(period);

                    return Reply.Document(
                        SalesReport.SalesCsv(totals, invoices, period),
                        "تقرير المبيعات — " + period.Label + "\n" +
                        invoices.Count + " فاتورة");
                }

                case "low":
                {
                    IReadOnlyList<LowStockLine> low = _pharmacy.LowStock();
                    if (low.Count == 0) return "لا يوجد صنف عند حد الطلب أو أقل.";

                    return Reply.Document(
                        SalesReport.LowStockCsv(low, DateTime.Now),
                        "تقرير المخزون المنخفض — " + low.Count + " صنف");
                }

                case "best":
                {
                    if (!Period.TryParse(command.Args.Length > 1 ? command.Args[1] : "month",
                            DateTime.Now, out Period period))
                        return ReportUsage;

                    IReadOnlyList<BestSellerRow> rows = _pharmacy.BestSellers(sender.ErpUserId, period, 200);
                    if (rows.Count == 0) return "لا مبيعات في هذه الفترة.";

                    return Reply.Document(
                        SalesReport.BestSellersCsv(rows, period),
                        "الأكثر مبيعاً — " + period.Label);
                }

                case "dead":
                {
                    IReadOnlyList<DeadStockRow> rows = _pharmacy.DeadStock(sender.ErpUserId, DeadStockDays);
                    if (rows.Count == 0)
                        return "لا يوجد مخزون راكد خلال " + DeadStockDays + " يوم.";

                    return Reply.Document(
                        SalesReport.DeadStockCsv(rows, DeadStockDays, DateTime.Now),
                        "المخزون الراكد — " + rows.Count + " صنف");
                }

                default:
                    return ReportUsage;
            }
        }
        catch (DomainException ex)
        {
            // Every report above is guarded by the ERP itself, and BestSellers and DeadStock refuse a
            // non-administrator outright. Its wording is the truthful one, so it is passed through
            // rather than replaced with something vaguer.
            return ex.Message;
        }
    }

    /// <summary>Days of no movement before stock counts as dead. The ERP's own default.</summary>
    public const int DeadStockDays = 60;

    private static string ReportUsage =>
        "الاستخدام: /report ثم النوع ثم الفترة\n" +
        "\n" +
        "الأنواع:\n" +
        "sales — المبيعات والفواتير\n" +
        "low — المخزون المنخفض\n" +
        "best — الأكثر مبيعاً\n" +
        "dead — المخزون الراكد\n" +
        "\n" +
        "الفترات: " + Period.Accepted + "\n" +
        "مثال: /report sales week";

    // ---------------- /link ----------------

    private Reply Link(CommandLine command, Sender sender)
    {
        string? code = command.FirstArg;
        if (string.IsNullOrWhiteSpace(code))
            return "الاستخدام: /link ثم رمز الربط\n" +
                   "مثال: /link 123456\n\n" +
                   "الرمز من برنامج دوائي: صفحة المدير ← إعداد تيليجرام.";

        LinkOutcome outcome = _linking.Redeem(code, sender.TelegramUserId, sender.ChatId);

        return outcome switch
        {
            LinkOutcome.Linked =>
                "تم الربط بنجاح.\n" +
                "هذا الحساب الآن مرتبط ببرنامج دوائي.\n\n" +
                "اكتب /help لعرض الأوامر.",

            // Deliberately the same wording for "never issued" and "expired". Telling a guesser which
            // of the two they hit is telling them whether a code exists — and the manager's remedy is
            // identical either way: generate a fresh one.
            LinkOutcome.NoSuchCode or LinkOutcome.Expired =>
                "رمز غير صالح أو انتهت صلاحيته.\n" +
                "أنشئ رمزاً جديداً من: صفحة المدير ← إعداد تيليجرام.",

            LinkOutcome.AlreadyUsed =>
                "هذا الرمز مُستخدم بالفعل.\n" +
                "كل رمز يُستخدم مرة واحدة فقط — أنشئ رمزاً جديداً.",

            LinkOutcome.NotAnAdmin =>
                "هذا الحساب لا يملك صلاحية المدير في برنامج دوائي.\n" +
                "هذا البوت للمدير فقط.",

            _ => "تعذّر إتمام الربط. حاول مرة أخرى.",
        };
    }

    private const string Help =
        "دوائي — بوت المدير\n" +
        "\n" +
        "الأوامر المتاحة:\n" +
        "/stock — المتوفر من صنف (بالباركود أو الاسم)\n" +
        "/low — الأصناف عند حد الطلب أو أقل\n" +
        "/help — هذه القائمة\n" +
        "/ping — التحقق من أن البوت يعمل\n" +
        "/link — ربط هذا الحساب ببرنامج دوائي\n" +
        "\n" +
        "قريباً: /sales و /report.\n" +
        "هذا البوت للمدير فقط، ولا يمكنه تعديل أي بيانات.";
}

/// <summary>
/// Redeeming a link code, behind a seam so the router can be tested without two databases.
/// </summary>
public interface ILinkService
{
    LinkOutcome Redeem(string code, long telegramUserId, long chatId);
}
