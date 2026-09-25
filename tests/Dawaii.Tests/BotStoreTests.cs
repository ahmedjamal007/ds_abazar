using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.Core.Bot;
using Dawaii.Core.Data;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Binding a Telegram account to a pharmacy account (V2.6).
    ///
    /// Six digits is not much of a secret, so it leans on three other things: ten minutes to live,
    /// one use only, and an active administrator behind it. Each of those is a separate test here,
    /// because losing any one of them turns the code into a million guesses against a bot that has
    /// no lockout — and what is behind it is the pharmacy's stock and its takings.
    ///
    /// Everything runs against a real SQLite file in a separate database, which is what the bot uses
    /// in production. The pharmacy's own database is never opened by any of this.
    /// </summary>
    [TestFixture]
    public class BotStoreTests
    {
        private string _path;
        private BotStore _store;

        private const int Manager = 7;          // users.id in the pharmacy's database
        private const long Phone = 111222333;   // a Telegram user id
        private const long Chat = 111222333;

        private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0);

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_bot_" + Guid.NewGuid().ToString("N") + ".db");
            _store = new BotStore(new SqliteConnectionFactory(_path));
            _store.EnsureSchema();
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static bool IsAdmin(int _) => true;
        private static bool NotAdmin(int _) => false;
        private static string RoleOf(int _) => "Admin";

        private LinkResult Redeem(string code, DateTime at, Func<int, bool> admin = null)
            => _store.Redeem(code, Phone, Chat, admin ?? IsAdmin, RoleOf, at);

        // ---------------- the schema ----------------

        [Test]
        public void TheSchema_CanBeAppliedTwice()
        {
            // An upgrade is the same operation as an install, so there is no migration state to fall
            // out of step. Running it again must be a no-op, not an error.
            Assert.DoesNotThrow(() => _store.EnsureSchema());
            Assert.DoesNotThrow(() => _store.EnsureSchema());

            Assert.That(_store.All(), Is.Empty);
            Assert.That(_store.HasToken(), Is.False);
        }

        // ---------------- issuing a code ----------------

        [Test]
        public void ACode_IsSixDigits()
        {
            string code = _store.CreateLinkCode(Manager, Now);

            Assert.That(code, Has.Length.EqualTo(6));
            Assert.That(code, Does.Match("^[0-9]{6}$"), "it has to be readable off a screen and retyped");
        }

        [Test]
        public void IssuingASecondCode_RetiresTheFirst()
        {
            string first = _store.CreateLinkCode(Manager, Now);
            string second = _store.CreateLinkCode(Manager, Now);

            Assert.That(Redeem(first, Now).Outcome, Is.EqualTo(LinkOutcome.AlreadyUsed),
                "a stale code left visible on a screen must stop working the moment a new one is made");
            Assert.That(Redeem(second, Now).Outcome, Is.EqualTo(LinkOutcome.Linked));
        }

        [Test]
        public void CodesAreNotSequential()
        {
            // Generated from a cryptographic source, not System.Random seeded off the clock: a
            // predictable code is no code at all.
            var codes = Enumerable.Range(0, 20)
                .Select(i => _store.CreateLinkCode(Manager + i, Now)).ToList();

            Assert.That(codes.Distinct().Count(), Is.EqualTo(codes.Count), "no repeats in twenty");
            Assert.That(codes.Zip(codes.Skip(1), (a, b) => int.Parse(b) - int.Parse(a)).Distinct().Count(),
                Is.GreaterThan(1), "consecutive codes must not differ by a constant");
        }

        // ---------------- redeeming one ----------------

        [Test]
        public void AGoodCode_BindsTheTelegramAccount()
        {
            string code = _store.CreateLinkCode(Manager, Now);

            LinkResult result = Redeem(code, Now);

            Assert.That(result.Success, Is.True);
            Assert.That(result.ErpUserId, Is.EqualTo(Manager));

            BotUser linked = _store.FindActive(Phone);
            Assert.That(linked, Is.Not.Null);
            Assert.That(linked.ErpUserId, Is.EqualTo(Manager));
            Assert.That(linked.ChatId, Is.EqualTo(Chat));
            Assert.That(linked.IsActive, Is.True);
        }

        [Test]
        public void ACodeThatWasNeverIssued_IsRefused()
        {
            Assert.That(Redeem("000000", Now).Outcome, Is.EqualTo(LinkOutcome.NoSuchCode));
            Assert.That(Redeem("abc", Now).Outcome, Is.EqualTo(LinkOutcome.NoSuchCode));
            Assert.That(Redeem("", Now).Outcome, Is.EqualTo(LinkOutcome.NoSuchCode));
            Assert.That(Redeem(null, Now).Outcome, Is.EqualTo(LinkOutcome.NoSuchCode));
            Assert.That(_store.FindActive(Phone), Is.Null);
        }

        [Test]
        public void ACodeOlderThanTenMinutes_IsRefused()
        {
            string code = _store.CreateLinkCode(Manager, Now);

            Assert.That(Redeem(code, Now.AddMinutes(9)).Outcome, Is.EqualTo(LinkOutcome.Linked));

            string second = _store.CreateLinkCode(Manager, Now);
            Assert.That(Redeem(second, Now.AddMinutes(11)).Outcome, Is.EqualTo(LinkOutcome.Expired),
                "a code left in a chat log must not still work an hour later");
        }

        [Test]
        public void ACodeUsedTwice_IsRefusedTheSecondTime()
        {
            string code = _store.CreateLinkCode(Manager, Now);

            Assert.That(Redeem(code, Now).Outcome, Is.EqualTo(LinkOutcome.Linked));
            Assert.That(_store.Redeem(code, 999888777, 999888777, IsAdmin, RoleOf, Now).Outcome,
                Is.EqualTo(LinkOutcome.AlreadyUsed),
                "single use is what makes a code read aloud or photographed survivable");

            Assert.That(_store.FindActive(999888777), Is.Null, "and the second phone is not bound");
        }

        [Test]
        public void ACodeWhoseAccountIsNoLongerAnAdmin_IsRefused()
        {
            // Checked at redemption, not only at issue: a code lives ten minutes, and an account can
            // be demoted or deactivated inside ten minutes.
            string code = _store.CreateLinkCode(Manager, Now);

            Assert.That(Redeem(code, Now, NotAdmin).Outcome, Is.EqualTo(LinkOutcome.NotAnAdmin));
            Assert.That(_store.FindActive(Phone), Is.Null);
        }

        [Test]
        public void ARefusedRedemption_DoesNotBurnTheCode()
        {
            string code = _store.CreateLinkCode(Manager, Now);

            Assert.That(Redeem(code, Now, NotAdmin).Outcome, Is.EqualTo(LinkOutcome.NotAnAdmin));

            // The manager gets their admin rights back and tries again with the same code. Burning it
            // on a failed attempt would mean a transient refusal costs them a trip to the admin page.
            Assert.That(Redeem(code, Now, IsAdmin).Outcome, Is.EqualTo(LinkOutcome.Linked));
        }

        // ---------------- revoking ----------------

        [Test]
        public void ARevokedAccount_IsNoLongerFound_ButIsStillOnRecord()
        {
            _store.Redeem(_store.CreateLinkCode(Manager, Now), Phone, Chat, IsAdmin, RoleOf, Now);

            _store.Revoke(Phone);

            Assert.That(_store.FindActive(Phone), Is.Null, "the bot stops answering them");
            Assert.That(_store.All().Any(u => u.TelegramUserId == Phone && !u.IsActive), Is.True,
                "but who used to have access stays answerable");
        }

        [Test]
        public void ARevokedAccount_CanBeLinkedAgain()
        {
            _store.Redeem(_store.CreateLinkCode(Manager, Now), Phone, Chat, IsAdmin, RoleOf, Now);
            _store.Revoke(Phone);

            _store.Redeem(_store.CreateLinkCode(Manager, Now), Phone, Chat, IsAdmin, RoleOf, Now);

            Assert.That(_store.FindActive(Phone), Is.Not.Null);
            Assert.That(_store.All().Count(u => u.TelegramUserId == Phone), Is.EqualTo(1),
                "re-linking replaces the binding rather than leaving two rows for one phone");
        }

        // ---------------- the token ----------------

        [Test]
        public void TheToken_IsStoredEncryptedAndReadBack()
        {
            const string token = "8012345678:AAH-not-a-real-token";

            Assert.That(_store.SaveToken(token), Is.True);
            Assert.That(_store.HasToken(), Is.True);
            Assert.That(_store.ReadToken(), Is.EqualTo(token));
        }

        [Test]
        public void TheTokenIsNotInTheFileInTheClear()
        {
            const string token = "8012345678:AAH-not-a-real-token";
            _store.SaveToken(token);

            SQLiteConnection.ClearAllPools();

            // Searched as BYTES, not as decoded text: Encoding.Latin1 does not exist on .NET
            // Framework, and this fixture runs on both targets.
            byte[] file = File.ReadAllBytes(_path);
            Assert.That(Contains(file, token), Is.False,
                "a token readable in the database file is a token anyone with the file can post as");
            Assert.That(Contains(file, "AAH-not-a-real"), Is.False);
        }

        [Test]
        public void AnEmptyToken_IsRefused_RatherThanStored()
        {
            Assert.That(_store.SaveToken(null), Is.False);
            Assert.That(_store.SaveToken(""), Is.False);
            Assert.That(_store.SaveToken("   "), Is.False);
            Assert.That(_store.HasToken(), Is.False);
        }

        [Test]
        public void TheToken_CanBeCleared()
        {
            _store.SaveToken("8012345678:AAH-x");
            _store.ClearToken();

            Assert.That(_store.HasToken(), Is.False);
            Assert.That(_store.ReadToken(), Is.Null);
        }

        // ---------------- the audit log ----------------

        [Test]
        public void ARefusedCommand_IsRecorded_BecauseNothingElseRecordsIt()
        {
            // An unknown sender gets silence from the bot, so this table is the only trace that
            // somebody tried the door.
            _store.Audit(999888777, "stock", "/stock panadol", success: false, note: "not linked", now: Now);

            var entries = _store.RecentAudit();
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].TelegramUserId, Is.EqualTo(999888777));
            Assert.That(entries[0].Success, Is.False);
            Assert.That(entries[0].Note, Is.EqualTo("not linked"));
        }

        [Test]
        public void TheAuditLog_ReadsNewestFirst()
        {
            _store.Audit(Phone, "help", "/help", true, null, Now);
            _store.Audit(Phone, "ping", "/ping", true, null, Now.AddSeconds(30));

            Assert.That(_store.RecentAudit()[0].Command, Is.EqualTo("ping"));
        }

        [Test]
        public void AVeryLongMessage_DoesNotBreakTheAuditLog()
        {
            Assert.DoesNotThrow(() =>
                _store.Audit(Phone, "stock", new string('x', 10_000), false, "nonsense", Now));

            Assert.That(_store.RecentAudit()[0].Command, Is.EqualTo("stock"));
        }

        /// <summary>Whether the file holds this text as plain ASCII bytes anywhere.</summary>
        private static bool Contains(byte[] haystack, string needle)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i + bytes.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < bytes.Length; j++)
                    if (haystack[i + j] != bytes[j]) { match = false; break; }
                if (match) return true;
            }
            return false;
        }
    }
}
