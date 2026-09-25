-- =====================================================================================
--  Dawaii Telegram bot — its own database (V2.6)
--
--  This runs against a SEPARATE SQLite file from the pharmacy's. Nothing here touches, or
--  is touched by, the ERP's own tables, and the bot never opens the pharmacy's database
--  for writing at all. That is the whole point of the separation: a 24/7 background
--  service cannot lock, corrupt or fill the one file the shop cannot afford to lose.
--
--  The cost is honest and worth stating: two files to back up instead of one, and the ERP
--  needs a small writer to push outbox rows across (phase 4). The alternative — bot_
--  tables inside the pharmacy's database — makes a background process a writer to live
--  pharmacy data, and on SQLite that is a lock-contention risk against the till.
--
--  Idempotent, in the same style as db/schema.sql: CREATE TABLE IF NOT EXISTS throughout,
--  so running it twice is harmless and an upgrade is the same operation as an install.
-- =====================================================================================

-- journal_mode is set per connection by SqliteConnectionFactory, and cannot be changed
-- from inside a transaction, which is how this script runs.
PRAGMA foreign_keys = ON;


-- -------------------------------------------------------------------------------------
--  Who may use the bot.
--
--  telegram_user_id is the NUMERIC Telegram account id and the only thing authorization
--  is ever decided on. Never a @username: a username can be given up and taken by
--  somebody else, which would silently hand a stranger the pharmacy's stock and takings.
--
--  erp_user_id points at users.id in the PHARMACY's database — deliberately not a foreign
--  key, because it is a different file. The bot re-reads that user's role live on every
--  command rather than trusting the role stored here, so an admin who is demoted or
--  deactivated loses the bot on their next message instead of at some future re-link.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bot_user (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  telegram_user_id INTEGER NOT NULL UNIQUE,          -- BIGINT: Telegram ids exceed 32 bits
  chat_id          INTEGER NOT NULL,                 -- where to send; not the same as the user
  erp_user_id      INTEGER NOT NULL,                 -- users.id in the pharmacy's database
  role             TEXT NOT NULL,                    -- role AT LINK TIME, for the audit trail only
  is_active        INTEGER NOT NULL DEFAULT 1,       -- 0 = revoked from the admin page
  linked_at        TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  last_seen_at     TEXT
);

CREATE INDEX IF NOT EXISTS ix_bot_user_erp ON bot_user(erp_user_id);


-- -------------------------------------------------------------------------------------
--  One-time codes that bind a Telegram account to an ERP account.
--
--  Generated on the admin page, typed by the manager as "/link 123456". Six digits is
--  short enough to read off a screen and retype on a phone, which is why the other three
--  defences matter: ten minutes to live, single use, and only an Admin may be linked at
--  all. Without those, six digits is 1,000,000 guesses and a bot has no lockout.
--
--  used_at is kept rather than the row deleted, so "somebody already used that code"
--  remains answerable afterwards.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bot_link_code (
  code        TEXT PRIMARY KEY,                      -- exactly 6 digits, as typed
  erp_user_id INTEGER NOT NULL,
  created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  expires_at  TEXT NOT NULL,
  used_at     TEXT,                                  -- NULL until redeemed; single use
  used_by     INTEGER                                -- the telegram_user_id that redeemed it
);

CREATE INDEX IF NOT EXISTS ix_bot_link_code_open ON bot_link_code(used_at, expires_at);


-- -------------------------------------------------------------------------------------
--  Messages the ERP wants delivered (filled in phase 4).
--
--  The outbox exists so the ERP never waits on Telegram and never loses a message to it.
--  The pharmacy inserts a row inside its own transaction; the worker picks it up and
--  delivers it. If the internet is down, or Telegram is down, or the PC was rebooted
--  mid-send, the row is simply still pending — which is the behaviour a low-stock alert
--  has to have to be worth anything.
--
--  sent_at NULL means pending. attempts and last_error make a stuck row diagnosable
--  instead of merely undelivered.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bot_outbox (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  audience   TEXT NOT NULL DEFAULT 'admins',         -- 'admins' or a specific telegram_user_id
  body       TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  sent_at    TEXT,                                   -- NULL = still pending
  attempts   INTEGER NOT NULL DEFAULT 0,
  last_error TEXT
);

-- The worker's only hot query: pending rows, oldest first. Partial index so a year of
-- delivered messages costs nothing to skip past.
CREATE INDEX IF NOT EXISTS ix_bot_outbox_pending
  ON bot_outbox(created_at) WHERE sent_at IS NULL;


-- -------------------------------------------------------------------------------------
--  Every command, including the ones that were refused.
--
--  The refusals are the point. An unknown Telegram account gets no reply from the bot —
--  silence, because any answer confirms to whoever is probing that it is real — so this
--  table is the ONLY place that attempt is recorded. A run of failed rows from one
--  unknown id is what somebody trying the bot's door looks like.
--
--  raw_text is stored to make a mistyped command explicable. It is a manager's own
--  message to their own pharmacy's bot, never a customer's.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bot_audit_log (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  telegram_user_id INTEGER NOT NULL,
  command          TEXT,                             -- the parsed verb, NULL if unparseable
  raw_text         TEXT,
  occurred_at      TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  success          INTEGER NOT NULL DEFAULT 0,
  note             TEXT                              -- why it was refused, when it was
);

CREATE INDEX IF NOT EXISTS ix_bot_audit_when ON bot_audit_log(occurred_at);
CREATE INDEX IF NOT EXISTS ix_bot_audit_who ON bot_audit_log(telegram_user_id, occurred_at);


-- -------------------------------------------------------------------------------------
--  The bot's own settings. Holds the Telegram token, DPAPI-protected under the machine
--  so the desktop app can write it and the Windows Service can read it back — see
--  Dawaii.Core.Data.MachineSecret. The token is never stored in the clear.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bot_setting (
  key        TEXT PRIMARY KEY,
  value      TEXT,
  updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
