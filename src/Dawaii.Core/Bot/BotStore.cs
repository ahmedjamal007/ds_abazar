using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Dawaii.Core.Data;

namespace Dawaii.Core.Bot
{
    /// <summary>
    /// The Telegram bot's own database (V2.6).
    ///
    /// A SEPARATE SQLite file from the pharmacy's. The bot never opens the pharmacy's database for
    /// writing at all, so a 24/7 background service cannot lock, corrupt or fill the one file the
    /// shop cannot afford to lose. The cost is two files to back up, and an ERP-side writer to push
    /// outbox rows across in phase 4 — both cheaper than a contended till.
    ///
    /// Shared by the desktop app, which writes the token and issues link codes from the admin page,
    /// and the bot service, which reads them. That is why it lives in Dawaii.Core: it is the only
    /// assembly both of them reference. It is not pharmacy domain and does not pretend to be.
    ///
    /// Core has no logging, so nothing here logs — every outcome is a return value.
    /// </summary>
    public sealed class BotStore
    {
        /// <summary>Six digits: short enough to read off a screen and retype on a phone.</summary>
        public const int CodeLength = 6;

        /// <summary>
        /// How long a link code lives. Ten minutes is the third of three defences, alongside single
        /// use and admin-only. Six digits on its own is a million guesses and a bot has no lockout.
        /// </summary>
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

        private const string TokenKey = "telegram_token_protected";

        private readonly IDbConnectionFactory _db;

        public BotStore(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Where the bot's database lives by default: beside the pharmacy's, under %ProgramData%, so
        /// every account on the PC and the Windows Service all reach the same file.
        /// </summary>
        public static string DefaultPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dawaii");
                return Path.Combine(dir, "dawaii.bot.db");
            }
        }

        /// <summary>
        /// Creates the tables if they are not there. Idempotent, like the pharmacy's own initializer:
        /// an upgrade is the same operation as an install, so there is no migration state to get
        /// wrong and no version table to fall out of step.
        /// </summary>
        public void EnsureSchema()
        {
            // The whole script in ONE command, the way DatabaseInitializer does it: SQLite executes
            // every statement in a batch itself. The first version of this split the script on
            // semicolons and was promptly broken by a semicolon inside a COMMENT, which cut a CREATE
            // TABLE in half — there is no reason to parse SQL when SQLite will.
            using (System.Data.Common.DbConnection conn = _db.OpenConnection())
            using (System.Data.Common.DbTransaction tx = conn.BeginTransaction())
            using (System.Data.Common.DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = ReadSchema();
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }

        // ---------------- the token ----------------

        /// <summary>
        /// Stores the Telegram token, encrypted for this machine. Returns false when it could not be
        /// protected, in which case NOTHING is stored — a token written in the clear would be worse
        /// than no token at all.
        /// </summary>
        public bool SaveToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;

            string sealedToken = MachineSecret.Protect(token.Trim(), MachineSecret.BotTokenPurpose);
            if (sealedToken == null) return false;

            Upsert(TokenKey, sealedToken);
            return true;
        }

        /// <summary>
        /// The token, or null when none is stored or it cannot be decrypted here — which is what a
        /// bot database copied from another machine looks like.
        /// </summary>
        public string ReadToken()
            => MachineSecret.Unprotect(Setting(TokenKey), MachineSecret.BotTokenPurpose);

        /// <summary>True when a token is stored, without decrypting it. For showing status on screen.</summary>
        public bool HasToken() => !string.IsNullOrWhiteSpace(Setting(TokenKey));

        public void ClearToken() => _db.Execute("DELETE FROM bot_setting WHERE key=@k", ("@k", TokenKey));

        // ---------------- linking ----------------

        /// <summary>
        /// Issues a one-time code for a pharmacy account. The caller has already established that the
        /// account is an active administrator; it is checked again at redemption, because a code
        /// lives for ten minutes and a lot can be revoked in ten minutes.
        /// </summary>
        public string CreateLinkCode(int erpUserId, DateTime now)
        {
            string code = SixDigits();

            // Retire any codes this account has outstanding. Two live codes for one person means a
            // stale one on a screen somewhere still works, which is exactly what single-use is for.
            _db.Execute(
                "UPDATE bot_link_code SET used_at=@t, used_by=0 " +
                "WHERE erp_user_id=@u AND used_at IS NULL",
                ("@t", Db.Time(now)), ("@u", erpUserId));

            _db.Execute(
                "INSERT INTO bot_link_code (code, erp_user_id, created_at, expires_at) " +
                "VALUES (@c, @u, @created, @expires)",
                ("@c", code), ("@u", erpUserId),
                ("@created", Db.Time(now)),
                ("@expires", Db.Time(now + CodeLifetime)));

            return code;
        }

        /// <summary>
        /// Redeems a code, binding a Telegram account to the pharmacy account it was issued for.
        /// </summary>
        /// <param name="isStillAnAdmin">
        /// Asked about the ERP account behind the code, at the moment of redemption. Passed in rather
        /// than looked up here because the answer lives in the PHARMACY's database, which this store
        /// deliberately never opens.
        /// </param>
        public LinkResult Redeem(string code, long telegramUserId, long chatId,
            Func<int, bool> isStillAnAdmin, Func<int, string> roleOf, DateTime now)
        {
            var result = new LinkResult();

            string normalized = (code ?? "").Trim();
            if (normalized.Length == 0)
            {
                result.Outcome = LinkOutcome.NoSuchCode;
                return result;
            }

            List<(int ErpUserId, DateTime Expires, bool Used)> rows = _db.Query(
                "SELECT erp_user_id, expires_at, used_at FROM bot_link_code WHERE code=@c",
                r => (Db.GetInt(r, "erp_user_id"), Db.GetTime(r, "expires_at"),
                      Db.GetTimeN(r, "used_at").HasValue),
                ("@c", normalized)) as List<(int, DateTime, bool)>
                ?? new List<(int, DateTime, bool)>();

            if (rows.Count == 0)
            {
                result.Outcome = LinkOutcome.NoSuchCode;
                return result;
            }

            (int erpUserId, DateTime expires, bool used) = rows[0];
            result.ErpUserId = erpUserId;

            // Used before expired: a code someone already redeemed is a more useful thing to be told
            // about than one that has also since lapsed.
            if (used) { result.Outcome = LinkOutcome.AlreadyUsed; return result; }
            if (now > expires) { result.Outcome = LinkOutcome.Expired; return result; }

            if (isStillAnAdmin == null || !isStillAnAdmin(erpUserId))
            {
                result.Outcome = LinkOutcome.NotAnAdmin;
                return result;
            }

            string role = roleOf?.Invoke(erpUserId) ?? "Admin";

            _db.Execute("UPDATE bot_link_code SET used_at=@t, used_by=@who WHERE code=@c",
                ("@t", Db.Time(now)), ("@who", telegramUserId), ("@c", normalized));

            // One Telegram account maps to one pharmacy account. Re-linking the same phone to a
            // different pharmacy login replaces the binding rather than creating a second one, and
            // re-activates it — which is how a revoked administrator is restored.
            _db.Execute(
                "INSERT INTO bot_user (telegram_user_id, chat_id, erp_user_id, role, is_active, linked_at) " +
                "VALUES (@tg, @chat, @u, @role, 1, @t) " +
                "ON CONFLICT(telegram_user_id) DO UPDATE SET " +
                "  chat_id=@chat, erp_user_id=@u, role=@role, is_active=1, linked_at=@t",
                ("@tg", telegramUserId), ("@chat", chatId), ("@u", erpUserId),
                ("@role", Db.Text(role)), ("@t", Db.Time(now)));

            result.Outcome = LinkOutcome.Linked;
            return result;
        }

        /// <summary>The binding for a Telegram account, or null when it has never linked or was revoked.</summary>
        public BotUser FindActive(long telegramUserId)
        {
            foreach (BotUser u in _db.Query(
                "SELECT telegram_user_id, chat_id, erp_user_id, role, is_active, linked_at, last_seen_at " +
                "FROM bot_user WHERE telegram_user_id=@tg AND is_active=1",
                ReadUser, ("@tg", telegramUserId)))
                return u;
            return null;
        }

        /// <summary>Everyone ever linked, revoked ones included, for the admin page's list.</summary>
        public IReadOnlyList<BotUser> All()
            => _db.Query(
                "SELECT telegram_user_id, chat_id, erp_user_id, role, is_active, linked_at, last_seen_at " +
                "FROM bot_user ORDER BY linked_at DESC",
                ReadUser);

        /// <summary>
        /// Revokes a Telegram account. The row is kept rather than deleted so the audit log still has
        /// something to point at, and so "who used to have access" stays answerable.
        /// </summary>
        public void Revoke(long telegramUserId)
            => _db.Execute("UPDATE bot_user SET is_active=0 WHERE telegram_user_id=@tg",
                ("@tg", telegramUserId));

        public void TouchSeen(long telegramUserId, DateTime now)
            => _db.Execute("UPDATE bot_user SET last_seen_at=@t WHERE telegram_user_id=@tg",
                ("@t", Db.Time(now)), ("@tg", telegramUserId));

        // ---------------- the outbox ----------------

        /// <summary>
        /// Queues a message for delivery.
        ///
        /// This is the whole point of the outbox: the caller's job finishes the moment the row is
        /// written. The pharmacy never waits on Telegram, never fails because Telegram is down, and
        /// never loses a message to a reboot — the row is simply still pending when the service comes
        /// back. An alert that only works when the internet does is not worth having.
        /// </summary>
        /// <param name="audience">
        /// <see cref="OutboxMessage.Admins"/> for every linked administrator, or a Telegram user id
        /// as a string for one person.
        /// </param>
        public void Enqueue(string audience, string body, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(body)) return;

            _db.Execute(
                "INSERT INTO bot_outbox (audience, body, created_at) VALUES (@a, @b, @t)",
                ("@a", Db.Text(string.IsNullOrWhiteSpace(audience) ? OutboxMessage.Admins : audience.Trim())),
                ("@b", Db.Text(body)),
                ("@t", Db.Time(now)));
        }

        /// <summary>
        /// Messages still to deliver, oldest first.
        ///
        /// Rows that have already failed <paramref name="maxAttempts"/> times are left out rather than
        /// retried forever. A message that cannot be delivered — a chat the manager deleted, a body
        /// Telegram rejects — would otherwise be picked up every five seconds for the life of the
        /// installation. Excluded rather than marked sent: it is NOT sent, and pretending otherwise
        /// would hide it. It stays visible as a stuck row with its last error, which is what makes it
        /// diagnosable.
        /// </summary>
        public IReadOnlyList<OutboxMessage> Pending(int maxAttempts, int limit)
            => _db.Query(
                "SELECT id, audience, body, created_at, attempts, last_error FROM bot_outbox " +
                "WHERE sent_at IS NULL AND attempts < @max ORDER BY created_at, id LIMIT @n",
                r => new OutboxMessage
                {
                    Id = Db.GetInt(r, "id"),
                    Audience = Db.GetStringN(r, "audience"),
                    Body = Db.GetStringN(r, "body"),
                    CreatedAt = Db.GetTime(r, "created_at"),
                    Attempts = Db.GetInt(r, "attempts"),
                    LastError = Db.GetStringN(r, "last_error"),
                },
                ("@max", maxAttempts), ("@n", limit));

        /// <summary>Marks a message delivered. It will never be selected again.</summary>
        public void MarkSent(int id, DateTime now)
            => _db.Execute("UPDATE bot_outbox SET sent_at=@t, last_error=NULL WHERE id=@id",
                ("@t", Db.Time(now)), ("@id", id));

        /// <summary>
        /// Records a failed delivery and counts the attempt. sent_at stays NULL, so the message is
        /// retried — until the attempt cap, after which it stays here as a stuck row rather than
        /// disappearing or being retried forever.
        /// </summary>
        public void MarkFailed(int id, string error)
            => _db.Execute(
                "UPDATE bot_outbox SET attempts = attempts + 1, last_error = @e WHERE id = @id",
                ("@e", Db.Text(Trim(error, 500))), ("@id", id));

        /// <summary>How the queue is doing, for the admin page: waiting, and given up on.</summary>
        public (int Pending, int Stuck) OutboxHealth(int maxAttempts)
        {
            int pending = 0, stuck = 0;
            foreach (var row in _db.Query(
                "SELECT " +
                "  SUM(CASE WHEN attempts <  @max THEN 1 ELSE 0 END) AS waiting, " +
                "  SUM(CASE WHEN attempts >= @max THEN 1 ELSE 0 END) AS stuck " +
                "FROM bot_outbox WHERE sent_at IS NULL",
                r => (Waiting: Db.GetIntN(r, "waiting") ?? 0, Stuck: Db.GetIntN(r, "stuck") ?? 0),
                ("@max", maxAttempts)))
            {
                pending = row.Waiting;
                stuck = row.Stuck;
            }
            return (pending, stuck);
        }

        /// <summary>Everyone a broadcast goes to: active links only.</summary>
        public IReadOnlyList<BotUser> ActiveRecipients()
            => _db.Query(
                "SELECT telegram_user_id, chat_id, erp_user_id, role, is_active, linked_at, last_seen_at " +
                "FROM bot_user WHERE is_active=1",
                ReadUser);

        // ---------------- remembering what has already been said ----------------

        /// <summary>
        /// A small piece of the bot's own state, by name. Used by the alert producers to remember what
        /// they have already sent, so a service restart does not re-announce yesterday's news.
        /// </summary>
        public string ReadMark(string key) => Setting("mark:" + key);

        public void WriteMark(string key, string value) => Upsert("mark:" + key, value);

        // ---------------- the audit log ----------------

        /// <summary>
        /// Records a command, including a refused one.
        ///
        /// The refusals are the point. An unknown Telegram account gets no reply — silence, because
        /// any answer confirms to whoever is probing that the bot is real — so this is the ONLY place
        /// that attempt is recorded anywhere.
        /// </summary>
        public void Audit(long telegramUserId, string command, string rawText, bool success,
            string note, DateTime now)
            => _db.Execute(
                "INSERT INTO bot_audit_log (telegram_user_id, command, raw_text, occurred_at, success, note) " +
                "VALUES (@tg, @cmd, @raw, @t, @ok, @note)",
                ("@tg", telegramUserId), ("@cmd", Db.Text(command)),
                ("@raw", Db.Text(Trim(rawText, 500))), ("@t", Db.Time(now)),
                ("@ok", success ? 1 : 0), ("@note", Db.Text(note)));

        /// <summary>The most recent entries, newest first — for the admin page.</summary>
        public IReadOnlyList<(long TelegramUserId, string Command, DateTime At, bool Success, string Note)>
            RecentAudit(int limit = 50)
            => _db.Query(
                "SELECT telegram_user_id, command, occurred_at, success, note FROM bot_audit_log " +
                "ORDER BY occurred_at DESC, id DESC LIMIT @n",
                r => (Db.GetLong(r, "telegram_user_id"), Db.GetStringN(r, "command"),
                      Db.GetTime(r, "occurred_at"), Db.GetBool(r, "success"), Db.GetStringN(r, "note")),
                ("@n", limit));

        // ---------------- plumbing ----------------

        private static BotUser ReadUser(System.Data.Common.DbDataReader r) => new BotUser
        {
            TelegramUserId = Db.GetLong(r, "telegram_user_id"),
            ChatId = Db.GetLong(r, "chat_id"),
            ErpUserId = Db.GetInt(r, "erp_user_id"),
            RoleAtLink = Db.GetStringN(r, "role"),
            IsActive = Db.GetBool(r, "is_active"),
            LinkedAt = Db.GetTime(r, "linked_at"),
            LastSeenAt = Db.GetTimeN(r, "last_seen_at"),
        };

        private string Setting(string key)
        {
            object v = _db.Scalar("SELECT value FROM bot_setting WHERE key=@k", ("@k", key));
            return v == null || v == DBNull.Value ? null : Convert.ToString(v);
        }

        private void Upsert(string key, string value)
            => _db.Execute(
                "INSERT INTO bot_setting (key, value, updated_at) VALUES (@k, @v, @t) " +
                "ON CONFLICT(key) DO UPDATE SET value=@v, updated_at=@t",
                ("@k", key), ("@v", Db.Text(value)), ("@t", Db.Time(DateTime.Now)));

        /// <summary>
        /// A six-digit code from a CRYPTOGRAPHIC source, not System.Random.
        ///
        /// Random is seeded from the clock and its output is predictable from any other draw, so an
        /// attacker who can make the admin page issue codes could narrow a million possibilities to a
        /// handful. The code is the only thing standing between a stranger's phone and the pharmacy's
        /// figures; it costs nothing to generate it properly.
        /// </summary>
        private static string SixDigits()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            // Mask the sign bit, then modulo. The bias across a million values is far below anything
            // that matters against a code that also expires in ten minutes and works once.
            uint value = (uint)(BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF);
            return (value % 1000000u).ToString("D6", CultureInfo.InvariantCulture);
        }

        private static string Trim(string s, int max)
            => s == null ? null : s.Length <= max ? s : s.Substring(0, max);

        private static string ReadSchema()
        {
            Assembly assembly = typeof(BotStore).Assembly;
            string name = assembly.GetName().Name + ".Sql.bot.schema.sql";
            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource missing: " + name);
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }

    }
}
