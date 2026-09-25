using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using Erp.TelegramBot.Telegram;

namespace Erp.TelegramBot.Commands;

/// <summary>Who sent a command, as much as any handler needs to know.</summary>
/// <param name="TelegramUserId">The numeric Telegram account. What authorization is decided on.</param>
/// <param name="ChatId">Where to reply. Not the same as the sender in a group.</param>
public readonly record struct Sender(long TelegramUserId, long ChatId);

/// <summary>What to say back, and any buttons to put under it.</summary>
public sealed record Reply(string Text, IReadOnlyList<Button>? Buttons = null)
{
    public static implicit operator Reply(string text) => new(text);
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
