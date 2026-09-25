namespace Erp.TelegramBot.Telegram;

/// <summary>A message someone sent the bot, stripped to what this program cares about.</summary>
/// <param name="UpdateId">Telegram's cursor. Acknowledged by asking for UpdateId + 1 next time.</param>
/// <param name="TelegramUserId">The SENDER's numeric id. What authorization is decided on.</param>
/// <param name="ChatId">Where to reply. Not the same as the sender in a group.</param>
/// <param name="Text">Message text; empty for a photo or sticker.</param>
public sealed record Incoming(int UpdateId, long TelegramUserId, long ChatId, string Text);

/// <summary>
/// Everything this bot does to Telegram, behind one seam.
///
/// The point is that nothing else in the project references Telegram.Bot. The parser, the
/// authorizer, the rate limiter and the router are then testable with no token, no network and no
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
    /// Long-polls for messages from <paramref name="offset"/> onward, holding the connection open
    /// until something arrives or the timeout expires. NEVER webhooks: this pharmacy has outbound
    /// internet and no public address, so nothing can reach in.
    /// </summary>
    Task<IReadOnlyList<Incoming>> PollAsync(int offset, TimeSpan timeout, CancellationToken ct);

    /// <summary>Sends a plain-text reply.</summary>
    Task SendAsync(long chatId, string text, CancellationToken ct);
}
