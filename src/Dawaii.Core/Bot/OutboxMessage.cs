using System;

namespace Dawaii.Core.Bot
{
    /// <summary>
    /// A message waiting to be delivered to Telegram.
    ///
    /// Written by whoever has something to say — the pharmacy application, or the bot's own alert
    /// producers — and delivered by the bot's NotificationWorker. Neither side has to be running when
    /// the other is: that is what the queue buys, and why an alert survives a reboot, a two-day
    /// internet outage, or the till being switched off for the weekend.
    /// </summary>
    public sealed class OutboxMessage
    {
        /// <summary>Audience meaning "every linked administrator".</summary>
        public const string Admins = "admins";

        public int Id { get; set; }

        /// <summary><see cref="Admins"/>, or a single Telegram user id as a string.</summary>
        public string Audience { get; set; }

        public string Body { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Failed deliveries so far. Past the cap the row is left alone, not retried forever.</summary>
        public int Attempts { get; set; }

        /// <summary>Why the last attempt failed, so a stuck message is diagnosable rather than merely late.</summary>
        public string LastError { get; set; }

        /// <summary>True when this is addressed to one person; their id is in <see cref="Audience"/>.</summary>
        public bool TryGetSingleRecipient(out long telegramUserId)
            => long.TryParse(Audience, out telegramUserId) && telegramUserId > 0;
    }
}
