namespace Erp.TelegramBot.Telegram;

/// <summary>A button under a message, and the token the bot gets back when it is pressed.</summary>
/// <param name="Label">What the manager reads.</param>
/// <param name="Data">
/// Opaque to Telegram, capped by it at 64 bytes. Treated as UNTRUSTED on the way back: it arrives
/// from a client and a tampered value must be refused, not obeyed.
/// </param>
public sealed record Button(string Label, string Data);

/// <summary>A button press coming back.</summary>
/// <param name="Id">Telegram's id for the press; must be acknowledged or the client spins.</param>
/// <param name="Data">Whatever was put in <see cref="Button.Data"/>. Untrusted.</param>
/// <param name="MessageId">The message the buttons were under, so it can be edited in place.</param>
public sealed record Callback(string Id, string Data, int MessageId);

/// <summary>A message someone sent the bot, stripped to what this program cares about.</summary>
/// <param name="UpdateId">Telegram's cursor. Acknowledged by asking for UpdateId + 1 next time.</param>
/// <param name="TelegramUserId">The SENDER's numeric id. What authorization is decided on.</param>
/// <param name="ChatId">Where to reply. Not the same as the sender in a group.</param>
/// <param name="Text">Message text; empty for a photo, a sticker, or a button press.</param>
/// <param name="Press">Set when this update is a button press rather than a message.</param>
public sealed record Incoming(
    int UpdateId, long TelegramUserId, long ChatId, string Text, Callback? Press = null);

/// <summary>
/// Everything this bot does to Telegram, behind one seam.
///
/// Nothing else in the project references Telegram.Bot. The parser, the authorizer, the rate
/// limiters, the router and every report are therefore testable with no token, no network and no
/// library types in the test project — and the day the client library makes a breaking change, one
/// adapter needs rewriting instead of the whole service.
/// </summary>
public interface ITelegramGateway
{
    /// <summary>
    /// This bot's @name, without the @. Needed to tell "/stock@ThisBot" from a command aimed at a
    /// different bot in the same group.
    /// </summary>
    Task<string> WhoAmIAsync(CancellationToken ct);

    /// <summary>
    /// Long-polls for updates from <paramref name="offset"/> onward, holding the connection open
    /// until something arrives or the timeout expires. NEVER webhooks: this pharmacy has outbound
    /// internet and no public address, so nothing can reach in.
    /// </summary>
    Task<IReadOnlyList<Incoming>> PollAsync(int offset, TimeSpan timeout, CancellationToken ct);

    /// <summary>Sends a reply, optionally with a row of buttons under it.</summary>
    Task SendAsync(long chatId, string text, IReadOnlyList<Button>? buttons, CancellationToken ct);

    /// <summary>
    /// Replaces the text and buttons of a message already sent — how paging works. Editing in place
    /// rather than sending a new message keeps the manager's chat from filling with near-identical
    /// copies of one list every time they press "next".
    /// </summary>
    Task EditAsync(long chatId, int messageId, string text, IReadOnlyList<Button>? buttons,
        CancellationToken ct);

    /// <summary>
    /// Tells Telegram a button press was received. Without this the manager's client shows a
    /// spinner on the button until it times out, which reads as a broken bot.
    /// </summary>
    Task AcknowledgeAsync(string callbackId, CancellationToken ct);
}
