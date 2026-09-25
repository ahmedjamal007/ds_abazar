using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Erp.TelegramBot.Pharmacy;

/// <summary>
/// An audit repository that refuses to write, given to the pharmacy services the bot borrows.
///
/// <c>InventoryService</c> and <c>CodeService</c> take an <see cref="IAuditRepository"/> because
/// their MUTATING methods record who changed what. The bot calls only their read methods, so nothing
/// should ever reach this — but "should" is doing a lot of work in that sentence, and the bot is a
/// 24/7 background process pointed at the pharmacy's live database.
///
/// So read-only is made structural rather than left as an intention. If a future change to this bot
/// calls a method that writes, it throws here, immediately, with a message that says exactly what
/// went wrong — instead of quietly appending rows to the pharmacy's audit log under a user who was
/// not there. Reads are allowed through, since reading the log is not writing to it.
/// </summary>
public sealed class RefusingAuditRepository : IAuditRepository
{
    public void Add(AuditEntry entry)
        => throw new InvalidOperationException(
            "The Telegram bot is read-only against pharmacy data and must not write an audit entry. " +
            "Something called a mutating service method; use the bot's own bot_audit_log instead.");

    public IReadOnlyList<AuditEntry> GetRecent(int limit) => [];
}
