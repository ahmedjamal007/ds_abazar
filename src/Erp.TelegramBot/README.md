# Dawaii Telegram bot — دوائي

A Telegram bot that lets the **pharmacy manager** check stock and pull reports from their phone.

Runs as a Windows Service on the pharmacy's own PC. It reaches out to Telegram; nothing reaches in.
No public IP, no open ports, no domain name, no webhooks.

**V2.8 — this is what exists today.** A reporting layer over the pharmacy system, driven by an
**Arabic menu** rather than by remembered commands: sales split by how the money actually arrived,
sales per employee, shifts, purchases, customer and supplier debts, expiry, and a price lookup you
reach by typing a drug name straight into the chat. Plus a **daily low-stock digest** through a
durable outbox. It reads the pharmacy's database and **never writes to it**.

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

**Normally you do not.** The Dawaii installer does it: tick **مساعد تيليجرام** during setup and it
registers the service, points it at this pharmacy's database, and starts it. Skip to section 6.

The rest of this section is for a hand install, or for a developer's machine.

The bot is published **self-contained**, deliberately. It is a .NET 10 worker while the program is
still .NET Framework 4.8, so a pharmacy PC has no .NET 10 runtime on it and generally no internet to
fetch one. A framework-dependent build installs cleanly and then fails at service start — the worst
of both, because it looks installed and answers nobody.

```
rm -rf dist/bot
dotnet publish src/Erp.TelegramBot -c Release -r win-x64 --self-contained true -o dist/bot
```

Delete the folder first. `dotnet publish -o` does not clean, and anything left behind from a previous
run is packaged into the installer and shipped to every pharmacy.

Then from an **Administrator** terminal:

```
sc.exe create DawaiiTelegramBot binPath= "C:\Dawaii\bot\Erp.TelegramBot.exe" start= auto
sc.exe description DawaiiTelegramBot "Dawaii pharmacy — Telegram bot for the manager"
sc.exe start DawaiiTelegramBot
```

The spaces after `binPath=` and `start=` are required by `sc.exe`. Quote the path.

**Set `ErpDirectory` before starting it** (section 3). A hand-installed bot sits in its own folder and
cannot guess where the pharmacy's `dawaii.ini` is, and pointed at the wrong database it does not fail
— it reports cheerfully empty stock. The installer writes this setting for you.

To remove it:

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

## Using it

**Type a drug name or scan a barcode straight into the chat.** No command. The price comes back in
boxes, strips and tablets with what is on the shelf. `*` on its own sends the whole price list as a
CSV. This is the thing the manager does twenty times a day, so it costs nothing to reach.

Everything else is on a **menu**, in Arabic, three screens deep at most:

| التقارير | العملاء والموردون | البحث |
|---|---|---|
| مبيعات اليوم | مديونية العملاء | قائمة الأسعار كاملة |
| مبيعات حسب الموظف | مديونية الموردين | المخزون المنخفض |
| تقرير الورديات | قائمة العملاء | |
| تقرير المشتريات | قائمة الموردين | |
| المخزون المنخفض | | |
| قرب الانتهاء | | |

The typed commands still work and are faster once learned — `/menu`, `/stock <صنف>`, `/low`,
`/sales today|week|month`, `/report`, `/ping`, `/link <code>` — but nobody has to know one. A manager
who never reads the help can use the whole bot, which is the difference between a tool that gets
opened and one that gets abandoned.

Every screen has a way back. A menu you can get lost in on a phone is a menu people stop opening.

### The reports

**Sales are split by how the money arrived** — نقدي, بنكك, فوري, أوكاش, آجل — because "today: 45,000"
is not an answer when the question is how much of it is actually in the drawer. The figures come from
`ShiftReconciliation`, the same code the till's own shift close uses, so the numbers reconcile with
the program rather than approximating it.

**A channel with nothing in it still shows as 0.00.** A missing line reads as missing data; a zero
reads as "none today", which is the answer.

**Employees are named, never user-named.** A report about a person says علاء الأمين, not `alaa01`.
The login name appears only when the account has no full name recorded, where it beats a blank.

**Profit appears only where the ERP already shows it** to that account. The bot re-uses the pharmacy's
own permission checks rather than reimplementing them, so a demoted account loses the figure here at
the same moment it loses it on screen.

**Lists are capped and say how many they cut**, every one of them — Telegram *refuses* a message over
4096 characters rather than truncating it, so an uncapped report is not a long report, it is no report
at all, and it fails first at the biggest pharmacy. A test holds every menu report against the limit
with hundreds of rows behind it.

### Shifts, and a limit stated rather than papered over

The shift report gives sign-in time and **time of last transaction**. It does not give a leaving time,
because **the system does not record one** — attendance stores a login and there is no logout anywhere
in the schema. Every shift report therefore ends with a line saying so.

This matters more than it looks: this report gets read by an owner deciding whether somebody left
early. Presenting a last-sale time under a heading like "الانصراف" would be inventing evidence about
an employee out of a gap in the data. An employee who signed in and sold nothing shows a dash.

### Purchases name who entered them

Each purchase shows the supplier, the representative, the total, and **which member of staff keyed it
in and when** — which is the question actually being asked when an owner opens a purchase report.

## Alerts

Once a day, at **09:00** by default, the manager gets a digest of everything at or below its reorder
level. Configure with `LowStockDigestHour`, or switch it off with `"LowStockDigest": false`.

It is a **summary** — the count, how many are out entirely, and the worst few named — with `/low`
for the full list. Forty drug names arriving unprompted at eight in the morning is how a manager
learns to stop reading the bot.

### Why it goes through an outbox

Nothing that raises an alert talks to Telegram. It writes a row to `bot_outbox` and its job is done;
the notification worker delivers it. That means:

- **A message survives an outage.** Raised at 2am while the internet was down, delivered when the
  connection returns — not lost.
- **It survives a reboot.** A Windows Service restarts whenever the PC does, and a pending row is
  still pending afterwards.
- **The pharmacy never waits on Telegram** and never fails because Telegram is slow.

A delivery that keeps failing is retried up to `OutboxMaxAttempts` (12), then **left alone rather
than marked sent** — because it was not sent, and pretending otherwise would hide a real failure.
It stays as a stuck row with its last error, and the admin page shows the count, so a message that
has been undeliverable for a fortnight is visible rather than merely absent.

The digest producer runs **in the bot**, not the desktop app. An alert producer in the app would only
run while the app is open, and the end of a shift is exactly when a manager still wants alerts. The
ERP can write outbox rows too — the queue is shared — so anything it wants to announce uses the same
delivery path.

### How quantities are reported

In **boxes and strips**, never in the single tablets the database stores. "1,247 tablets" is a
number nobody can picture; "12 boxes + 4 strips" is a thing you can walk over and look at. The exact
unit total is given in brackets, because that is the figure which reconciles with every other screen
in the program.

A level of packaging is only reported when the catalogue actually records it. A drug with no strip
size is reported in plain units rather than having packaging invented for it.

### `/stock` shows batches, not warehouses

This ERP has no warehouse concept and never has. The useful breakdown is by **batch** — lot number
and expiry date, soonest first, which is the order the till sells them in. It is also the more
valuable answer: it tells a manager not just how much they have but how much of it is about to be
unsellable. Anything expiring within 90 days says how many days are left.

### `/low` pages, and that is not cosmetic

Telegram **refuses** a message over 4096 characters — it does not truncate it. A pharmacy with sixty
drugs below their threshold would receive nothing at all and see a bot that ignored them, so the
feature would break precisely at the pharmacies that need it most. Ten items a page, with buttons to
walk through, and the page size is held against the limit by a test rather than chosen by eye.

Pressing a button **edits the message in place** rather than sending another, so paging through does
not fill the manager's chat with near-identical copies of one list.

## The database

`db/bot.schema.sql` — plain SQL, readable before it runs, applied at startup and by the admin page.
Idempotent (`CREATE TABLE IF NOT EXISTS` throughout), so an upgrade is the same operation as an
install and there is no migration state to fall out of step.

Five tables — `bot_user`, `bot_link_code`, `bot_outbox`, `bot_audit_log`, `bot_setting` — in a
**separate database** from the pharmacy's. **The bot never opens the pharmacy's database for
writing.** A 24/7 background service cannot lock or corrupt the one file the shop cannot lose. The
cost, stated honestly: two files to back up, and an ERP-side writer for the outbox rows the desktop app queues.

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
- **Read-only is structural, not a promise.** The pharmacy services the bot borrows are handed an
  audit repository that THROWS on write. If a future change calls a mutating method it fails
  immediately with a message saying so, rather than quietly appending rows to the pharmacy's audit
  log under a user who was not there.
- **Button presses are authorized like any command**, and the data coming back from the client is
  treated as untrusted: a stale or forged page number produces a valid page, never an exception.

## Tests

```
dotnet test tests/Erp.TelegramBot.Tests
dotnet test tests/Dawaii.Tests --framework net10.0-windows
```

178 bot tests over the authorization gate, both rate limiters, the command parser, the backoff
curve, the paging arithmetic, the digest's restraint, the menu, and every report — that the payment
split is complete including its zeroes, that employees are named and not user-named, that a shift's
end time is never invented, and that no report can outgrow one Telegram message. The polling loop is
driven end to end through a fake Telegram, because the ORDER those checks run in is the security
property and no unit test underneath it would notice a price lookup that answered before asking who
was asking. Plus 68 in the ERP
suite over link codes, the outbox lifecycle, token sealing, machine-secret purposes and the
box/strip division.

No token and no network needed: everything Telegram-specific sits behind one interface
(`ITelegramGateway`), and only `TelegramGateway.cs` references the client library.
