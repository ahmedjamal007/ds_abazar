namespace Erp.TelegramBot.Commands;

/// <summary>
/// Turns a parsed command into the text to send back.
///
/// Returns a string rather than sending one, and knows nothing about Telegram, so every command's
/// wording can be asserted in a test. Null means "say nothing" — which is a real answer, not a
/// failure: an unknown sender must get silence.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>
    /// Telegram refuses a message over 4096 characters. Phase 1 sends nothing near it, but the limit
    /// is stated here because /low and /report will have to paginate or send a document instead, and
    /// the number belongs next to the thing that must respect it.
    /// </summary>
    public const int TelegramTextLimit = 4096;

    public string? Handle(CommandLine command)
    {
        if (!command.IsCommand) return null;      // ordinary chatter is not an error

        return command.Verb switch
        {
            "start" or "help" => Help,
            "ping" => "pong",
            _ => $"أمر غير معروف: /{command.Verb}\nاكتب /help لعرض الأوامر.",
        };
    }

    private const string Help =
        "دوائي — بوت المدير\n" +
        "\n" +
        "الأوامر المتاحة الآن:\n" +
        "/help — هذه القائمة\n" +
        "/ping — التحقق من أن البوت يعمل\n" +
        "\n" +
        "قريباً: /link و /stock و /low و /sales و /report.\n" +
        "هذا البوت للمدير فقط، ولا يمكنه تعديل أي بيانات.";
}
