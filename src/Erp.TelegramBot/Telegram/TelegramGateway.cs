using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Erp.TelegramBot.Telegram;

/// <summary>
/// The only file in this project that knows Telegram.Bot exists.
///
/// Thin on purpose — translation and nothing else. No routing, no authorization, no business rules,
/// so that none of those become untestable by association.
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
            // Messages and button presses only. Asking for everything would have the bot wake for
            // edits, reactions and chat-member churn it has no handler for, and acknowledge them as
            // if they were handled.
            allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
            cancellationToken: ct);

        var result = new List<Incoming>(updates.Length);
        foreach (Update u in updates)
        {
            if (u.CallbackQuery is { } press)
            {
                // A press can arrive for a message Telegram no longer gives us (very old, or deleted).
                // Without a message id there is nothing to edit, so it is dropped — but the update id
                // is still returned or it would be polled again forever.
                Message? source = press.Message;
                result.Add(new Incoming(
                    u.Id,
                    press.From.Id,
                    source?.Chat.Id ?? 0,
                    string.Empty,
                    source is null ? null : new Callback(press.Id, press.Data ?? string.Empty, source.MessageId)));
                continue;
            }

            Message? m = u.Message;

            // A sticker, a photo, a service notice about someone joining. Nothing to route, but the
            // update id still has to be returned.
            if (m?.From is null)
            {
                result.Add(new Incoming(u.Id, 0, m?.Chat.Id ?? 0, string.Empty));
                continue;
            }

            result.Add(new Incoming(u.Id, m.From.Id, m.Chat.Id, m.Text ?? string.Empty));
        }
        return result;
    }

    public Task SendAsync(long chatId, string text, IReadOnlyList<Button>? buttons, CancellationToken ct)
        => _client.SendMessage(chatId, text, replyMarkup: Markup(buttons), cancellationToken: ct);

    public async Task EditAsync(long chatId, int messageId, string text,
        IReadOnlyList<Button>? buttons, CancellationToken ct)
    {
        try
        {
            await _client.EditMessageText(chatId, messageId, text,
                replyMarkup: Markup(buttons), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Telegram refuses an edit that would not change anything ("message is not modified"),
            // which happens when the manager presses the page they are already on. That is not a
            // fault worth surfacing — the acknowledgement already stopped their spinner.
            _log.LogDebug(ex, "Edit of message {MessageId} was refused.", messageId);
        }
    }

    public async Task SendDocumentAsync(long chatId, string fileName, byte[] content,
        string? caption, CancellationToken ct)
    {
        // The stream wraps the bytes already in memory and is disposed here; nothing touches disk.
        using var stream = new MemoryStream(content, writable: false);

        await _client.SendDocument(
            chatId,
            InputFile.FromStream(stream, fileName),
            caption: caption,
            cancellationToken: ct);
    }

    public async Task AcknowledgeAsync(string callbackId, CancellationToken ct)
    {
        try
        {
            await _client.AnswerCallbackQuery(callbackId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Acknowledgements expire. A press answered late throws, and there is nothing useful to
            // do about it — the button has already stopped spinning on its own.
            _log.LogDebug(ex, "Callback {CallbackId} could not be acknowledged.", callbackId);
        }
    }

    private static InlineKeyboardMarkup? Markup(IReadOnlyList<Button>? buttons)
    {
        if (buttons is null || buttons.Count == 0) return null;

        // Two to a row. One per row wastes a phone screen on a menu this size; three across
        // truncates Arabic labels, which is worse than scrolling.
        var rows = buttons
            .Select((b, i) => (Button: b, Index: i))
            .GroupBy(x => x.Index / 2)
            .Select(g => g.Select(x =>
                InlineKeyboardButton.WithCallbackData(x.Button.Label, x.Button.Data)));

        return new InlineKeyboardMarkup(rows);
    }
}
