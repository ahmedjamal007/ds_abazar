using Dawaii.Core.Bot;

namespace Erp.TelegramBot.Commands;

/// <summary>Who sent a command, as much as any handler needs to know.</summary>
/// <param name="TelegramUserId">The numeric Telegram account. What authorization is decided on.</param>
/// <param name="ChatId">Where to reply. Not the same as the sender in a group.</param>
public readonly record struct Sender(long TelegramUserId, long ChatId);

/// <summary>
/// Turns a parsed command into the text to send back.
///
/// Returns a string rather than sending one, so every command's wording can be asserted in a test
/// with no token and no network. Null means "say nothing" — a real answer, not a failure.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>
    /// Telegram refuses a message over 4096 characters. /low and /report will have to paginate or
    /// send a document; the number lives next to the thing that must respect it.
    /// </summary>
    public const int TelegramTextLimit = 4096;

    private readonly ILinkService _linking;

    public CommandRouter(ILinkService linking)
    {
        _linking = linking;
    }

    /// <summary>
    /// The commands an UNLINKED sender may use. Everything else from someone the bot does not know
    /// gets silence, so a stranger cannot even establish that the bot is real.
    ///
    /// /link has to be here: it is how a manager becomes known in the first place.
    /// </summary>
    public static bool IsOpenToStrangers(string verb) => verb == "link";

    public string? Handle(CommandLine command, Sender sender)
    {
        if (!command.IsCommand) return null;      // ordinary chatter is not an error

        return command.Verb switch
        {
            "start" or "help" => Help,
            "ping" => "pong",
            "link" => Link(command, sender),
            _ => "أمر غير معروف: /" + command.Verb + "\nاكتب /help لعرض الأوامر.",
        };
    }

    private string Link(CommandLine command, Sender sender)
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

            // Deliberately the same wording for "never issued" and "expired". Telling a guesser
            // which of the two they hit is telling them whether a code exists — and the manager's
            // remedy is identical either way: generate a fresh one.
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
        "الأوامر المتاحة الآن:\n" +
        "/help — هذه القائمة\n" +
        "/ping — التحقق من أن البوت يعمل\n" +
        "/link — ربط هذا الحساب ببرنامج دوائي\n" +
        "\n" +
        "قريباً: /stock و /low و /sales و /report.\n" +
        "هذا البوت للمدير فقط، ولا يمكنه تعديل أي بيانات.";
}

/// <summary>
/// Redeeming a link code, behind a seam so the router can be tested without two databases.
/// </summary>
public interface ILinkService
{
    LinkOutcome Redeem(string code, long telegramUserId, long chatId);
}
