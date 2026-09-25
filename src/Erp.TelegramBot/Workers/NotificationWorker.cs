using Dawaii.Core.Bot;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.TelegramBot.Workers;

/// <summary>
/// Delivers whatever is waiting in the outbox.
///
/// The second hosted service in the same process as the command loop, and deliberately independent
/// of it: a manager sending commands has nothing to do with an alert going out, and neither should
/// be able to stall the other.
///
/// The queue is the point. Whoever had something to say wrote a row and finished — the pharmacy
/// never waits on Telegram, never fails because Telegram is down, and never loses a message to a
/// reboot. This worker picks up whatever is pending whenever it can, which means an alert raised at
/// two in the morning during an outage is delivered when the connection returns rather than lost.
/// </summary>
public sealed class NotificationWorker : BackgroundService
{
    private readonly BotStore _bot;
    private readonly ITelegramGateway _telegram;
    private readonly BotOptions _options;
    private readonly ILogger<NotificationWorker> _log;

    private readonly Random _jitter = new();
    private int _consecutiveFailures;

    /// <summary>Delivered per pass. Enough to clear a backlog briskly without blocking for minutes.</summary>
    private const int BatchSize = 20;

    public NotificationWorker(
        BotStore bot,
        ITelegramGateway telegram,
        IOptions<BotOptions> options,
        ILogger<NotificationWorker> log)
    {
        _bot = bot;
        _telegram = telegram;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Notification worker starting; polling the outbox every {Seconds}s.",
            _options.OutboxPollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait = _options.OutboxPollInterval;

            try
            {
                int delivered = await DeliverAsync(stoppingToken);
                _consecutiveFailures = 0;

                // A backlog is drained without waiting the full interval between batches — after a
                // long outage there may be a lot, and the manager wants it now, not in an hour.
                if (delivered >= BatchSize) wait = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The whole pass failed — the bot's own database is unreachable, most likely. Back
                // off rather than spin, and never die: the queue is still there when it recovers.
                _consecutiveFailures++;
                wait = RetryDelay.For(_consecutiveFailures, _jitter.NextDouble());
                _log.LogWarning(ex,
                    "Outbox pass failed ({Failures} in a row). Retrying in {Seconds:0.#}s.",
                    _consecutiveFailures, wait.TotalSeconds);
            }

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Notification worker stopped.");
    }

    /// <summary>Delivers one batch. Returns how many rows were dealt with, successfully or not.</summary>
    private async Task<int> DeliverAsync(CancellationToken ct)
    {
        IReadOnlyList<OutboxMessage> pending = _bot.Pending(_options.OutboxMaxAttempts, BatchSize);
        if (pending.Count == 0) return 0;

        foreach (OutboxMessage message in pending)
        {
            if (ct.IsCancellationRequested) break;
            await DeliverOneAsync(message, ct);
        }
        return pending.Count;
    }

    private async Task DeliverOneAsync(OutboxMessage message, CancellationToken ct)
    {
        List<long> chats;
        try
        {
            chats = Recipients(message);
        }
        catch (Exception ex)
        {
            _bot.MarkFailed(message.Id, "recipients: " + ex.Message);
            _log.LogWarning(ex, "Could not work out who outbox message {Id} is for.", message.Id);
            return;
        }

        if (chats.Count == 0)
        {
            // Nobody to send it to. Counted as a failed attempt rather than dropped, because the
            // usual cause is temporary — nobody has linked a phone YET — and the alert is still worth
            // delivering to whoever links tomorrow. The attempt cap stops it being retried forever.
            _bot.MarkFailed(message.Id, "no active recipients");
            _log.LogInformation(
                "Outbox message {Id} has no active recipients; it will be retried.", message.Id);
            return;
        }

        var failures = new List<string>();
        int sent = 0;

        foreach (long chatId in chats)
        {
            try
            {
                await _telegram.SendAsync(chatId, message.Body, null, ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down mid-broadcast. Leave the row pending: at worst a manager who already
                // received it gets it once more, which is far better than one who needed it not
                // getting it at all.
                return;
            }
            catch (Exception ex)
            {
                failures.Add(chatId + ": " + ex.Message);
            }
        }

        if (sent > 0)
        {
            // Delivered to at least one manager. Marked sent even if another's chat rejected it —
            // one blocked account must not make the whole pharmacy's alerts retry forever. The
            // failures are logged so a consistently unreachable manager is still findable.
            _bot.MarkSent(message.Id, DateTime.Now);

            if (failures.Count > 0)
                _log.LogWarning("Outbox message {Id} reached {Sent} of {Total}: {Failures}",
                    message.Id, sent, chats.Count, string.Join("; ", failures));
            else
                _log.LogInformation("Outbox message {Id} delivered to {Sent} recipient(s).",
                    message.Id, sent);
            return;
        }

        string error = string.Join("; ", failures);
        _bot.MarkFailed(message.Id, error);
        _log.LogWarning("Outbox message {Id} could not be delivered: {Error}", message.Id, error);
    }

    /// <summary>
    /// The chats a message goes to.
    ///
    /// A broadcast reaches every ACTIVE link, so revoking a manager stops their alerts as well as
    /// their commands — one revocation, not two places to remember.
    /// </summary>
    private List<long> Recipients(OutboxMessage message)
    {
        IReadOnlyList<BotUser> active = _bot.ActiveRecipients();

        if (message.TryGetSingleRecipient(out long one))
            return active.Where(u => u.TelegramUserId == one).Select(u => u.ChatId).ToList();

        return active.Select(u => u.ChatId).Distinct().ToList();
    }
}
