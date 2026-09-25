# Dawaii Telegram bot — دوائي

A Telegram bot that lets the **pharmacy manager** check stock and pull reports from their phone.

Runs as a Windows Service on the pharmacy's own PC. It reaches out to Telegram; nothing reaches in.
No public IP, no open ports, no domain name, no webhooks.

**Phase 2 — this is what exists today.** The bot connects, links a manager's phone to their Dawaii
account with a one-time code, and answers `/start`, `/help`, `/ping` and `/link`. It reads the
pharmacy's database only to check who is asking; there are no commands that report data yet.

---

## 1. Create the bot with BotFather

1. In Telegram, open a chat with **@BotFather**.
2. Send `/newbot`. Give it a display name (e.g. `Dawaii Manager`) and a username ending in `bot`.
3. BotFather replies with a **token** like `8012345678:AAH...`.

**That token is a password.** Anyone holding it can post as your bot and read everything sent to it.
It never goes in this repository, in a screenshot, or in a chat message. If it leaks, send `/revoke`
to BotFather and issue a new one — that is the only way to stop it.

While you are there, `/setprivacy` → **Enable** stops the bot receiving every message in any group
it is added to.

## 2. Set it up from inside Dawaii

Open Dawaii as the manager, then **إعداد تيليجرام** in the sidebar.

1. **إدخال الرمز السري للبوت** — paste the token. It is sealed with DPAPI under the *machine*, so the
   Windows Service can read it back, and it is never displayed again afterwards.
2. **رمز ربط جديد** — generates a six-digit code, good for **10 minutes** and **one use only**.
3. From Telegram, send the bot `/link 123456`. It confirms the link.
4. The page lists every linked account and lets you revoke one, which takes effect on that phone's
   very next message.

Each manager generates their own code while signed in as themselves. An administrator cannot mint
one for a colleague — the link binds a phone to *that* Dawaii account, and the audit trail has to
name the right person.

Restart the service after changing the token:

```
sc.exe stop DawaiiTelegramBot
sc.exe start DawaiiTelegramBot
```

There is no bootstrap problem to solve: the admin page lives inside Dawaii and is reached by signing
in as a manager, so the first code is generated exactly like every one after it.

## 3. Configuration file

`appsettings.json`, next to the executable:

```json
{
  "Bot": {
    "ErpDirectory": "C:\\Program Files\\Dawaii",
    "BotDatabasePath": "",
    "PollTimeoutSeconds": 30,
    "RateLimitPerMinute": 20,
    "LinkAttemptsPerMinute": 10
  }
}
```

**`ErpDirectory` is the one that matters.** It is the folder holding the pharmacy's `dawaii.ini`,
normally its install directory. The bot runs from its own folder, so it has to be told. Left empty,
the bot assumes a default local install — right for most shops, wrong in network mode, and **a bot
pointed at the wrong database reports cheerfully empty stock.** The service logs which database it
opened at startup; read that line once and you will never wonder.

`BotDatabasePath` empty means `%ProgramData%\Dawaii\dawaii.bot.db`, beside the pharmacy's, so the
desktop app and the service reach the same file.

### The token for local development

Without the admin page, the token can come from an environment variable:

```
setx ERP_TELEGRAM_TOKEN "8012345678:AAH..." /M
```

`/M` sets it machine-wide, which a Windows Service needs. Open a **new** terminal afterwards.
`Bot:Token` in `appsettings.json` also works, for local development only.

**The admin page wins over both.** Once a manager has set the token in the program, that is the
authoritative copy — a stale variable left on the machine from setup must not silently override it.

With nobody linked the bot starts, polls, and ignores every message. It logs a warning saying
exactly that, because "the bot ignores me" gives no hint of the cause.

## 4. Run it from a console first

```
dotnet run --project src/Erp.TelegramBot
```

Send `/ping`. It should reply `pong`. If it does, the token, the connection, the link and the reply
path all work.

If it stays silent, read the console: every refused message is logged with the sender's numeric ID.

## 5. Install as a Windows Service

```
dotnet publish src/Erp.TelegramBot -c Release -o C:\Dawaii\bot
```

Then from an **Administrator** terminal:

```
sc.exe create DawaiiTelegramBot binPath= "C:\Dawaii\bot\Erp.TelegramBot.exe" start= auto
sc.exe description DawaiiTelegramBot "Dawaii pharmacy — Telegram bot for the manager"
sc.exe start DawaiiTelegramBot
```

The spaces after `binPath=` and `start=` are required by `sc.exe`. Quote the path. To remove it:

```
sc.exe stop DawaiiTelegramBot
sc.exe delete DawaiiTelegramBot
```

As a service the bot also writes to the **Windows Event Log** under `Dawaii Telegram Bot`, since
nobody is watching a console.

### Two things that catch people out

**The service runs as `LocalSystem`, not as you.** This is why the token is sealed machine-scoped
rather than per-user — a per-user secret written by the manager's login is one `LocalSystem`
genuinely cannot read. The pharmacy's MySQL password has been stored the same way since V2.3, for
the same reason.

**The bot is a separate process from the till.** Deliberately: the desktop app gets closed at the
end of a shift and the manager still wants to ask about stock. Closing Dawaii does not stop the bot,
and restarting the PC starts it again on its own.

## Commands

| command | what it does |
|---|---|
| `/start`, `/help` | usage |
| `/ping` | replies `pong` — proves the bot is alive |
| `/link <code>` | binds this Telegram account to a Dawaii manager account |

Coming in later phases: `/stock`, `/low`, `/sales`, `/report`.

## The database

`db/bot.schema.sql` — plain SQL, readable before it runs, applied at startup and by the admin page.
Idempotent (`CREATE TABLE IF NOT EXISTS` throughout), so an upgrade is the same operation as an
install and there is no migration state to fall out of step.

Five tables — `bot_user`, `bot_link_code`, `bot_outbox`, `bot_audit_log`, `bot_setting` — in a
**separate database** from the pharmacy's. **The bot never opens the pharmacy's database for
writing.** A 24/7 background service cannot lock or corrupt the one file the shop cannot lose. The
cost, stated honestly: two files to back up, and an ERP-side writer for outbox rows in phase 4.

To apply it by hand:

```
sqlite3 "%ProgramData%\Dawaii\dawaii.bot.db" < db\bot.schema.sql
```

## How it behaves

- **Unknown senders get silence.** Not an error message — silence. Any reply confirms to a stranger
  that the bot is real and listening. The attempt goes to `bot_audit_log`, which is the only record
  it happened; a run of refused rows from one id is what somebody trying the door looks like.
- **`/link` is the one exception**, because it is how a manager becomes known. It therefore has its
  own throttle, **global rather than per-sender**: a per-sender limit would have the bot remember
  every Telegram id that ever messaged it, which anyone can grow without bound from fresh accounts —
  a rate limiter that is itself the denial of service.
- **Link codes lean on three things**, since six digits is not a secret on its own: ten minutes to
  live, one use only, and an active administrator behind it — re-checked at redemption, not just at
  issue, because a lot can be revoked in ten minutes. Issuing a new code retires the old one.
- **Roles are re-checked live on every command.** A manager who is demoted, deactivated or dismissed
  loses the bot on their next message, not whenever somebody remembers to revoke them. If the
  pharmacy's database cannot be reached, the answer is **no**.
- **Commands are rate limited** to 20 a minute per person. A refused command does not count against
  the allowance, or anyone who tripped the limit would keep it tripped by continuing to try.
- **Network failures back off** exponentially from 2 seconds to a 5-minute ceiling, with jitter. A
  pharmacy's connection dropping is normal operation, not a fault.
- **One bad message never blocks the rest.** The update cursor advances even when handling throws.

## Tests

```
dotnet test tests/Erp.TelegramBot.Tests
dotnet test tests/Dawaii.Tests --framework net10.0-windows
```

54 bot tests over the authorization gate, both rate limiters, the command parser, the backoff curve
and every reply; plus 30 in the ERP suite over link codes, token sealing and the machine-secret
purposes. No token and no network needed: everything Telegram-specific sits behind one interface
(`ITelegramGateway`), and only `TelegramGateway.cs` references the client library.
