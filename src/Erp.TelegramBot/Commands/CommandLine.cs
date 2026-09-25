namespace Erp.TelegramBot.Commands;

/// <summary>
/// One line of text from Telegram, taken apart.
///
/// Parsing is separated from everything that acts on the result so the awkward cases can be pinned
/// by tests rather than discovered at a pharmacy: Telegram appends "@BotName" to commands sent in a
/// group, people type trailing spaces and capitals, and most messages are not commands at all.
/// </summary>
public readonly struct CommandLine
{
    /// <summary>The command without its slash, lower-cased. Empty when this is not a command.</summary>
    public string Verb { get; }

    /// <summary>Whitespace-separated arguments after the verb. Never null.</summary>
    public string[] Args { get; }

    /// <summary>True only for text that actually began with a slash and named something.</summary>
    public bool IsCommand => Verb.Length > 0;

    private CommandLine(string verb, string[] args)
    {
        Verb = verb;
        Args = args;
    }

    /// <summary>Nothing to act on — blank text, or a customer's message that is not a command.</summary>
    public static CommandLine None { get; } = new(string.Empty, []);

    /// <summary>The first argument, or null when none was given.</summary>
    public string? FirstArg => Args.Length > 0 ? Args[0] : null;

    /// <summary>Every argument joined back with single spaces — for a drug name typed as several words.</summary>
    public string Rest => string.Join(' ', Args);

    /// <summary>
    /// Takes a message apart.
    /// </summary>
    /// <param name="text">Raw message text; null and whitespace are fine.</param>
    /// <param name="botUsername">
    /// This bot's @name, without the @. Telegram turns "/stock" into "/stock@DawaiiBot" in groups, and
    /// a command addressed to a DIFFERENT bot in the same group must be ignored rather than answered.
    /// </param>
    public static CommandLine Parse(string? text, string? botUsername = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        string trimmed = text.Trim();
        if (trimmed[0] != '/') return None;

        string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string head = parts[0][1..];                       // drop the slash
        if (head.Length == 0) return None;                 // a bare "/" is not a command

        int at = head.IndexOf('@');
        if (at >= 0)
        {
            string addressee = head[(at + 1)..];
            head = head[..at];
            if (head.Length == 0) return None;

            // Addressed to another bot in a group: not ours to answer.
            if (!string.IsNullOrEmpty(botUsername) &&
                !addressee.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
                return None;
        }

        return new CommandLine(head.ToLowerInvariant(), parts[1..]);
    }
}
