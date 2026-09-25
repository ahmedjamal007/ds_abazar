using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Erp.TelegramBot.Telegram;

/// <summary>
/// The only file in this project that knows Telegram.Bot exists.
///
/// Keep it thin on purpose — translation and nothing else. No routing, no authorization, no
/// business rules, so that none of those become untestable by association.
/// </summary>
public sealed class TelegramGateway : ITelegramGateway
{
    private readonly ITelegramBotClient _client;
    private readonly ILogger<TelegramGateway> _log;

    public TelegramGateway(ITelegramBotClient client, ILogger<TelegramGateway> log)
    {
        _client = client;
        _log = log;
    }

    public async Task<string> WhoAmIAsync(CancellationToken ct)
    {
        User me = await _client.GetMe(ct);
        return me.Username ?? string.Empty;
    }

    public async Task<IReadOnlyList<Incoming>> PollAsync(int offset, TimeSpan timeout, CancellationToken ct)
    {
        Update[] updates = await _client.GetUpdates(
            offset: offset,
            limit: 100,
            timeout: (int)timeout.TotalSeconds,
            // Only messages. Asking for everything would have the bot wake for edits, reactions and
            // chat-member churn it has no handler for, and acknowledge them as if they were handled.
            allowedUpdates: [UpdateType.Message],
            cancellationToken: ct);

        var result = new List<Incoming>(updates.Length);
        foreach (Update u in updates)
        {
            Message? m = u.Message;

            // A sticker, a photo, a service notice about someone joining. There is nothing to route,
            // but the update id still has to be returned or it would be polled again forever.
            if (m?.From is null)
            {
                result.Add(new Incoming(u.Id, 0, m?.Chat.Id ?? 0, string.Empty));
                continue;
            }

            result.Add(new Incoming(u.Id, m.From.Id, m.Chat.Id, m.Text ?? string.Empty));
        }
        return result;
    }

    public Task SendAsync(long chatId, string text, CancellationToken ct)
        => _client.SendMessage(chatId, text, cancellationToken: ct);
}
