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
/// nothing can reach in. This is also why the bot works behind whatever router the shop has without
/// anybody configuring a port.
///
/// The loop's contract with itself: it must never die and must never spin. A pharmacy runs this as a
/// service for months without anyone looking at it, so every failure is caught, logged once, and
/// waited out on a backoff curve.
/// </summary>
public sealed class CommandWorker : BackgroundService
{
    private readonly ITelegramGateway _telegram;
    private readonly CommandAuthorizer _authorizer;
    private readonly CommandRouter _router;
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
        CommandRouter router,
        IOptions<BotOptions> options,
        ILogger<CommandWorker> log)
    {
        _telegram = telegram;
        _authorizer = authorizer;
        _router = router;
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
                    // Advance the cursor whatever happens to this message. A message that throws must
                    // not be re-delivered forever — that is how a bot gets stuck on one bad input and
                    // stops answering anybody.
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

                // Logged at warning, not error: a pharmacy's internet dropping for a minute is
                // expected operation, and an error for every poll would bury the real faults.
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
        CommandLine command = CommandLine.Parse(message.Text, _botUsername);
        if (!command.IsCommand) return;     // a sticker, or someone chatting

        Access access = _authorizer.Check(message.TelegramUserId, DateTimeOffset.Now);

        if (access == Access.Denied)
        {
            // Deliberately no reply. Any answer at all confirms to a stranger that the bot is real
            // and listening. Phase 2 writes this to bot_audit_log; for now the service log is the
            // only record, and it is the record that matters for noticing someone probing.
            _log.LogWarning(
                "Ignored /{Verb} from unauthorized Telegram user {TelegramUserId} in chat {ChatId}.",
                command.Verb, message.TelegramUserId, message.ChatId);
            return;
        }

        if (access == Access.RateLimited)
        {
            _log.LogInformation("Rate-limited Telegram user {TelegramUserId}.", message.TelegramUserId);
            await _telegram.SendAsync(message.ChatId,
                "أوامر كثيرة في وقت قصير. انتظر دقيقة ثم أعد المحاولة.", ct);
            return;
        }

        string? reply = _router.Handle(command);
        if (reply is null) return;

        _log.LogInformation("/{Verb} from {TelegramUserId}.", command.Verb, message.TelegramUserId);
        await _telegram.SendAsync(message.ChatId, reply, ct);
    }
}
