using System;

namespace Dawaii.Core.Bot
{
    /// <summary>A Telegram account bound to a pharmacy account.</summary>
    public sealed class BotUser
    {
        public long TelegramUserId { get; set; }
        public long ChatId { get; set; }

        /// <summary>users.id in the PHARMACY's database — a different file, so not a foreign key.</summary>
        public int ErpUserId { get; set; }

        /// <summary>
        /// The role this account had WHEN IT WAS LINKED, kept for the audit trail only.
        ///
        /// Never authorize on it. The bot re-reads the role from the pharmacy's database on every
        /// command, so an administrator who is demoted or deactivated loses the bot on their next
        /// message rather than whenever somebody thinks to re-link them.
        /// </summary>
        public string RoleAtLink { get; set; }

        public bool IsActive { get; set; }
        public DateTime LinkedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }

    /// <summary>How an attempt to redeem a link code ended.</summary>
    public enum LinkOutcome
    {
        /// <summary>Bound. The Telegram account can now use the bot.</summary>
        Linked,

        /// <summary>Six digits that were never issued — or were issued on another machine.</summary>
        NoSuchCode,

        /// <summary>Issued, but more than its lifetime ago.</summary>
        Expired,

        /// <summary>Already redeemed. Single use is the point: a code read off a screen may be seen.</summary>
        AlreadyUsed,

        /// <summary>
        /// The code is good but the pharmacy account behind it is no longer an active administrator.
        /// Checked at redemption as well as at issue, because a code lives for ten minutes and a lot
        /// can be revoked in ten minutes.
        /// </summary>
        NotAnAdmin,
    }

    /// <summary>The result of redeeming a code, with the account bound when it succeeded.</summary>
    public sealed class LinkResult
    {
        public LinkOutcome Outcome { get; set; }
        public int ErpUserId { get; set; }
        public bool Success => Outcome == LinkOutcome.Linked;
    }
}
