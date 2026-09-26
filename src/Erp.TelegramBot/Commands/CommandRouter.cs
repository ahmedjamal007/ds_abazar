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

    public CommandRouter(ILinkService linking, IPharmacyReader pharmacy)
    {
        _linking = linking;
        _pharmacy = pharmacy;
    }

    /// <summary>
    /// The commands an UNLINKED sender may use. Everything else from someone the bot does not know
    /// gets silence, so a stranger cannot even establish that the bot is real.
    ///
    /// /link has to be here: it is how a manager becomes known in the first place.
    /// </summary>
    public static bool IsOpenToStrangers(string verb) => verb == "link";

    public Reply? Handle(CommandLine command, Sender sender)
    {
        if (!command.IsCommand) return null;      // ordinary chatter is not an error

        return command.Verb switch
        {
            "start" or "help" => Help,
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
    public Reply? HandlePress(string callbackData)
    {
        if (callbackData is null || !callbackData.StartsWith(LowPagePrefix, StringComparison.Ordinal))
            return null;

        string raw = callbackData[LowPagePrefix.Length..];
        if (!int.TryParse(raw, out int page)) return null;

        return Low(page);
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
