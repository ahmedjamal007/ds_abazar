# Dawaii Telegram bot — دوائي

A Telegram bot that lets the **pharmacy manager** check stock and pull reports from their phone.

Runs as a Windows Service on the pharmacy's own PC. It reaches out to Telegram; nothing reaches in.
No public IP, no open ports, no domain name, no webhooks.

**Phase 1 — this is what exists today.** The bot connects, authorizes by numeric Telegram user ID,
and answers `/start`, `/help` and `/ping`. It touches **no pharmacy data at all** and has no
database connection. The point of this phase is to prove the whole path works before anything goes
near the till's data.

---

## 1. Create the bot with BotFather

1. In Telegram, open a chat with **@BotFather**.
2. Send `/newbot`. Give it a display name (e.g. `Dawaii Manager`) and a username ending in `bot`
   (e.g. `DawaiiManagerBot`).
3. BotFather replies with a **token** that looks like `8012345678:AAH...`.

**That token is a password.** Anyone holding it can post as your bot and read everything sent to it.
It never goes in this repository, in a screenshot, or in a chat message. If it leaks, send
`/revoke` to BotFather and issue a new one — that is the only way to stop it.

While you are there, `/setprivacy` → **Enable** stops the bot receiving every message in any group
it is added to.

## 2. Find your numeric Telegram user ID

Authorization is by **numeric ID only, never @username** — a username can be given up and taken by
someone else, which would silently hand them access to your pharmacy's figures.

Message **@userinfobot** in Telegram. It replies with your `Id`, a number like `111222333`.

## 3. Configure

The token comes from an environment variable:

```
setx ERP_TELEGRAM_TOKEN "8012345678:AAH..." /M
```

`/M` sets it machine-wide, which a Windows Service needs. Open a **new** terminal afterwards — the
existing one will not see it.

Then put your user ID in `appsettings.json` next to the executable:

```json
{
  "Bot": {
    "AdminTelegramUserIds": [ 111222333 ],
    "PollTimeoutSeconds": 30,
    "RateLimitPerMinute": 20
  }
}
```

`Bot:Token` in `appsettings.json` also works, **for local development only**. The environment
variable always wins, so a developer's file can never override the real token on a pharmacy's PC.

With nobody in `AdminTelegramUserIds` the bot starts, polls, and ignores every message — it logs a
warning saying exactly that, because "the bot ignores me" gives no hint of the cause.

> From phase 2 this moves into the app: **Admin page → Telegram setup**, where the token is stored
> encrypted machine-scoped so the service can read it back, and administrators are linked with a
> one-time code instead of being typed into a file.

## 4. Run it from a console first

```
dotnet run --project src/Erp.TelegramBot
```

Message your bot `/ping`. It should reply `pong`. If it does, the token, the connection, the
whitelist and the reply path all work — which is the whole purpose of phase 1.

If it stays silent, check the console. Every rejected message is logged with the sender's numeric
ID, so an ID that does not match your `appsettings.json` entry is immediately obvious.

## 5. Install as a Windows Service

Publish it first:

```
dotnet publish src/Erp.TelegramBot -c Release -o C:\Dawaii\bot
```

Then, from an **Administrator** terminal:

```
sc.exe create DawaiiTelegramBot binPath= "C:\Dawaii\bot\Erp.TelegramBot.exe" start= auto
sc.exe description DawaiiTelegramBot "Dawaii pharmacy — Telegram bot for the manager"
sc.exe start DawaiiTelegramBot
```

The spaces after `binPath=` and `start=` are required by `sc.exe`. Quote the path.

To stop and remove it:

```
sc.exe stop DawaiiTelegramBot
sc.exe delete DawaiiTelegramBot
```

Running as a service, the bot also writes to the **Windows Event Log** under
`Dawaii Telegram Bot`, since nobody is watching a console.

### Two things that catch people out

**The service runs as `LocalSystem`, not as you.** A `setx` without `/M` sets the variable for your
account only and the service will not see it. This is also why the token, from phase 2 onward, is
encrypted machine-scoped rather than user-scoped — the way the MySQL password currently is, which
`LocalSystem` genuinely cannot decrypt.

**The bot is a separate process from the till.** That is deliberate: the desktop app gets closed at
the end of a shift, and the manager still wants to be able to ask about stock. Closing Dawaii does
not stop the bot, and restarting the PC starts it again on its own.

## Commands

| command | what it does |
|---|---|
| `/start`, `/help` | usage |
| `/ping` | replies `pong` — proves the bot is alive |

Coming in later phases: `/link`, `/stock`, `/low`, `/sales`, `/report`.

## Migrations

**None yet.** Phase 1 has no database of any kind. Phase 2 adds four `bot_` tables in a **separate**
database from the pharmacy's, as a plain `.sql` script you can read before running it. The bot never
writes to the ERP's own database, so it cannot lock or corrupt the pharmacy's data.

## How it behaves

- **Unknown senders get silence.** Not an error message — silence. Any reply at all confirms to a
  stranger that the bot is real and listening. The attempt is logged with the numeric ID.
- **Rate limited** to 20 commands a minute per person, with a message saying so. A refused command
  does not count against the allowance, or someone who tripped the limit would keep it tripped by
  continuing to try.
- **Network failures back off** exponentially from 2 seconds to a 5-minute ceiling, with jitter. A
  pharmacy's connection dropping is normal operation, not a fault; the bot must not spin, fill the
  disk with logs, or get its token throttled for hammering Telegram.
- **One bad message never blocks the rest.** The update cursor advances even when handling throws,
  so the bot cannot get stuck on a single input and stop answering everybody.

## Tests

```
dotnet test tests/Erp.TelegramBot.Tests
```

41 tests covering the authorization gate, the rate limiter, the command parser, the backoff curve and
the replies. No token and no network needed: everything Telegram-specific sits behind one interface
(`ITelegramGateway`), and only `TelegramGateway.cs` references the client library.
