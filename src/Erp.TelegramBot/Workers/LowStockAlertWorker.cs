using Dawaii.Core.Bot;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Reports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.TelegramBot.Workers;

/// <summary>
/// Tells the manager, once a day, what has fallen to its reorder level.
///
/// The hard part of this feature is not finding low stock — /low already does that. It is NOT
/// SENDING IT FORTY TIMES. An alert that arrives every five minutes is an alert a manager mutes
/// within a week, and a muted alert is worse than none: they believe they are being watched over
/// when they are not.
///
/// So: one digest a day, at a configured hour, and the date of the last one is remembered in the
/// bot's own database rather than in memory. Restarting the service does not re-announce what was
/// already said this morning, which matters because a Windows Service restarts whenever the PC does.
///
/// It only ever WRITES TO THE OUTBOX. It does not talk to Telegram, so a network outage is somebody
/// else's problem: the digest is queued and delivered when the connection returns.
/// </summary>
public sealed class LowStockAlertWorker : BackgroundService
{
    private const string Mark = "low_stock_digest";

    private readonly BotStore _bot;
    private readonly IPharmacyReader _pharmacy;
    private readonly BotOptions _options;
    private readonly ILogger<LowStockAlertWorker> _log;

    public LowStockAlertWorker(
        BotStore bot,
        IPharmacyReader pharmacy,
        IOptions<BotOptions> options,
        ILogger<LowStockAlertWorker> log)
    {
        _bot = bot;
        _pharmacy = pharmacy;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LowStockDigest)
        {
            _log.LogInformation("Daily low-stock digest is switched off.");
            return;
        }

        _log.LogInformation("Daily low-stock digest enabled, at {Hour:00}:00.", _options.LowStockDigestHour);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Consider(DateTime.Now);
            }
            catch (Exception ex)
            {
                // Never dies. A pharmacy database that is briefly unreachable must not cost the
                // digest permanently — it simply tries again on the next pass.
                _log.LogWarning(ex, "Low-stock digest check failed; will try again.");
            }

            // Checked every few minutes rather than sleeping until the exact hour: a PC that is asleep
            // or switched off at the appointed minute would otherwise skip the day entirely, and a
            // pharmacy's computer is off more often than it is on overnight.
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sends today's digest if it is due and has not been sent. Public so the decision can be tested
    /// by handing it a time instead of waiting for one.
    /// </summary>
    public void Consider(DateTime now)
    {
        // Checked here as well as at the loop. The loop is the only caller today, but a switch that
        // only works on one path is a switch somebody will later find does not work.
        if (!_options.LowStockDigest) return;

        if (now.Hour < _options.LowStockDigestHour) return;      // too early in the day

        string today = now.ToString("yyyy-MM-dd");
        if (_bot.ReadMark(Mark) == today) return;                // already said this morning

        IReadOnlyList<LowStockLine> low = _pharmacy.LowStock();

        // Nothing wrong is not news. Marked as done regardless, so a healthy pharmacy is not
        // re-checked every five minutes for the rest of the day.
        if (low.Count == 0)
        {
            _bot.WriteMark(Mark, today);
            _log.LogInformation("Nothing at or below reorder level; no digest sent.");
            return;
        }

        _bot.Enqueue(OutboxMessage.Admins, Compose(low, now), now);

        // Written AFTER queueing. The other order would risk a day of silence if the process died in
        // between; this order risks at worst a duplicate, and a duplicate alert is a far smaller
        // failure than a missing one.
        _bot.WriteMark(Mark, today);

        _log.LogInformation("Queued the daily low-stock digest: {Count} item(s).", low.Count);
    }

    /// <summary>
    /// The digest.
    ///
    /// Deliberately a SUMMARY with the worst few named, not the whole list. The full list is one
    /// command away with /low, and forty drug names arriving unprompted at eight in the morning is
    /// how a manager learns to stop reading the bot. It also has to fit in one Telegram message.
    /// </summary>
    private string Compose(IReadOnlyList<LowStockLine> low, DateTime now)
    {
        int outOfStock = low.Count(l => l.IsOut);

        var sb = new System.Text.StringBuilder();
        sb.Append("تنبيه المخزون — ").Append(now.ToString("yyyy-MM-dd")).Append('\n').Append('\n');
        sb.Append(low.Count).Append(" صنف عند حد الطلب أو أقل.");

        if (outOfStock > 0)
            sb.Append('\n').Append("⛔ ").Append(outOfStock).Append(" منها نفد تماماً.");

        sb.Append('\n').Append('\n');

        // Worst first — the reader already ordered them proportionally, so the ones named here are
        // the ones actually closest to running out, not merely the smallest numbers.
        foreach (LowStockLine line in low.Take(_options.LowStockDigestNames))
            sb.Append(line.IsOut ? "⛔ " : "• ").Append(line.Item.DisplayName).Append('\n');

        if (low.Count > _options.LowStockDigestNames)
            sb.Append("… و").Append(low.Count - _options.LowStockDigestNames).Append(" غيرها.\n");

        sb.Append('\n').Append("اكتب /low لعرض القائمة كاملة.");
        return sb.ToString();
    }
}
