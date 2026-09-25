using Dawaii.Core.Bot;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Security;
using Erp.TelegramBot.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.TelegramBot.Workers;

/// <summary>
/// The polling loop: ask Telegram for messages, decide whether the sender may be answered, answer.
///
/// Long polling, never webhooks — the pharmacy has outbound internet and no public address, so
/// nothing can reach in, and the bot works behind whatever router the shop has with no port to
/// configure.
///
/// The loop's contract with itself: never die, never spin. A pharmacy runs this as a service for
/// months without anyone looking at it, so every failure is caught, logged once, and waited out on a
/// backoff curve.
/// </summary>
public sealed class CommandWorker : BackgroundService
{
    private readonly ITelegramGateway _telegram;
    private readonly CommandAuthorizer _authorizer;
    private readonly LinkThrottle _linkThrottle;
    private readonly CommandRouter _router;
    private readonly BotStore _bot;
    private readonly BotOptions _options;
    private readonly ILogger<CommandWorker> _log;

    /// <summary>Jitter source. Not security-sensitive — it only spreads retries apart.</summary>
    private readonly Random _jitter = new();

    private int _offset;
    private int _consecutiveFailures;
    private string _botUsername = string.Empty;

    public CommandWorker(
        ITelegramGateway telegram,
        CommandAuthorizer authorizer,
        LinkThrottle linkThrottle,
        CommandRouter router,
        BotStore bot,
        IOptions<BotOptions> options,
        ILogger<CommandWorker> log)
    {
        _telegram = telegram;
        _authorizer = authorizer;
        _linkThrottle = linkThrottle;
        _router = router;
        _bot = bot;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Telegram command worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_botUsername.Length == 0)
                {
                    _botUsername = await _telegram.WhoAmIAsync(stoppingToken);
                    _log.LogInformation("Connected to Telegram as @{BotUsername}.", _botUsername);
                }

                IReadOnlyList<Incoming> batch =
                    await _telegram.PollAsync(_offset, _options.PollTimeout, stoppingToken);

                // Reset only after a poll actually succeeded, so a run of failures keeps backing off.
                _consecutiveFailures = 0;

                foreach (Incoming message in batch)
                {
                    // Advance the cursor whatever happens. A message that throws must not be
                    // re-delivered forever — that is how a bot gets stuck on one bad input and stops
                    // answering everybody.
                    _offset = Math.Max(_offset, message.UpdateId + 1);

                    try
                    {
                        await HandleAsync(message, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed handling update {UpdateId}.", message.UpdateId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;      // ordinary shutdown, not a fault
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                TimeSpan wait = RetryDelay.For(_consecutiveFailures, _jitter.NextDouble());

                // Warning, not error: a pharmacy's internet dropping for a minute is expected
                // operation, and an error per poll would bury the real faults.
                _log.LogWarning(ex,
                    "Telegram poll failed ({Failures} in a row). Retrying in {Seconds:0.#}s.",
                    _consecutiveFailures, wait.TotalSeconds);

                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInformation("Telegram command worker stopped.");
    }

    private async Task HandleAsync(Incoming message, CancellationToken ct)
    {
        if (message.Press is { } press)
        {
            await HandlePressAsync(message, press, ct);
            return;
        }

        CommandLine command = CommandLine.Parse(message.Text, _botUsername);
        if (!command.IsCommand) return;     // a sticker, or someone chatting

        var sender = new Sender(message.TelegramUserId, message.ChatId);
        DateTimeOffset now = DateTimeOffset.Now;

        // ---- linking: the ONE thing a sender the bot does not know may do ----
        //
        // It has to be, because it is how a manager becomes known. It is therefore also the only
        // place a stranger can make the bot work, and the only place a six-digit code can be guessed
        // at — hence a throttle of its own, global so that a flood cannot grow any per-sender state.
        if (CommandRouter.IsOpenToStrangers(command.Verb) && !_authorizer.IsKnown(message.TelegramUserId))
        {
            if (!_linkThrottle.Allow(now))
            {
                _log.LogWarning("Link attempts throttled; ignoring one from {TelegramUserId}.",
                    message.TelegramUserId);
                Audit(message, command, success: false, note: "link throttled");
                return;     // silence: a guesser learns nothing from being told to slow down
            }

            Reply? linkReply = _router.Handle(command, sender);
            Audit(message, command, success: true, note: "link attempt");
            if (linkReply != null)
                await _telegram.SendAsync(message.ChatId, linkReply.Text, linkReply.Buttons, ct);
            return;
        }

        Access access = _authorizer.Check(message.TelegramUserId, now);

        if (access == Access.Denied)
        {
            // Deliberately no reply. Any answer at all confirms to a stranger that the bot is real
            // and listening. The audit row is the ONLY record this happened — a run of these from one
            // unknown id is what somebody trying the door looks like.
            _log.LogWarning(
                "Ignored /{Verb} from unauthorized Telegram user {TelegramUserId} in chat {ChatId}.",
                command.Verb, message.TelegramUserId, message.ChatId);
            Audit(message, command, success: false, note: "not an authorized administrator");
            return;
        }

        if (access == Access.RateLimited)
        {
            _log.LogInformation("Rate-limited Telegram user {TelegramUserId}.", message.TelegramUserId);
            Audit(message, command, success: false, note: "rate limited");
            await _telegram.SendAsync(message.ChatId,
                "أوامر كثيرة في وقت قصير. انتظر دقيقة ثم أعد المحاولة.", null, ct);
            return;
        }

        Reply? reply = _router.Handle(command, sender);
        Audit(message, command, success: true, note: null);
        TouchSeen(message.TelegramUserId);

        if (reply == null) return;

        _log.LogInformation("/{Verb} from {TelegramUserId}.", command.Verb, message.TelegramUserId);
        await _telegram.SendAsync(message.ChatId, reply.Text, reply.Buttons, ct);
    }

    /// <summary>
    /// A paging button press.
    ///
    /// Acknowledged FIRST, whatever happens next: until Telegram is told the press arrived, the
    /// manager's client shows a spinner on the button, which reads as a bot that has hung. Then the
    /// same authorization as any command — a button in an old chat is still a request, and the person
    /// pressing it may have been revoked since it was sent.
    /// </summary>
    private async Task HandlePressAsync(Incoming message, Callback press, CancellationToken ct)
    {
        await _telegram.AcknowledgeAsync(press.Id, ct);

        if (_authorizer.Check(message.TelegramUserId, DateTimeOffset.Now) != Access.Allowed)
        {
            _log.LogWarning("Ignored a button press from unauthorized Telegram user {TelegramUserId}.",
                message.TelegramUserId);
            AuditPress(message, press, success: false, note: "not an authorized administrator");
            return;
        }

        Reply? reply = _router.HandlePress(press.Data);
        AuditPress(message, press, success: reply != null, note: reply == null ? "unknown button" : null);
        if (reply == null) return;

        // Edited in place rather than sent again, so pressing "next" five times does not leave five
        // near-identical copies of the list in the manager's chat.
        await _telegram.EditAsync(message.ChatId, press.MessageId, reply.Text, reply.Buttons, ct);
    }

    private void AuditPress(Incoming message, Callback press, bool success, string? note)
    {
        try
        {
            _bot.Audit(message.TelegramUserId, "button", press.Data, success, note, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write the bot audit log.");
        }
    }

    /// <summary>
    /// Writes the audit row. Never allowed to break the reply: a bot that stops answering because its
    /// own log is unwritable is worse than one with a gap in the log, and the service log still has it.
    /// </summary>
    private void Audit(Incoming message, CommandLine command, bool success, string? note)
    {
        try
        {
            _bot.Audit(message.TelegramUserId, command.IsCommand ? command.Verb : null,
                message.Text, success, note, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write the bot audit log.");
        }
    }

    private void TouchSeen(long telegramUserId)
    {
        try { _bot.TouchSeen(telegramUserId, DateTime.Now); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not record last-seen."); }
    }
}
