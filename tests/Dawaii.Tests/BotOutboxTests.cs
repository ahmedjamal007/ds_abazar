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
    /// The outbox (V2.6): messages queued by whoever has something to say, delivered by the bot.
    ///
    /// The queue exists so neither side waits on the other. The pharmacy writes a row and its job is
    /// done; Telegram being down, the PC rebooting, or the till being off for the weekend all become
    /// a delay rather than a lost message. An alert that only works when the internet does is not
    /// worth having.
    ///
    /// The cases that matter here are the awkward ones: a message nobody can receive yet, a message
    /// that will never be deliverable, and making sure neither is silently dropped nor retried until
    /// the end of time.
    /// </summary>
    [TestFixture]
    public class BotOutboxTests
    {
        private const int MaxAttempts = 5;
        private static readonly DateTime Now = new(2026, 9, 26, 9, 0, 0);

        private string _path;
        private BotStore _store;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_outbox_" + Guid.NewGuid().ToString("N") + ".db");
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

        private OutboxMessage Only() => _store.Pending(MaxAttempts, 50).Single();

        // ---------------- queueing ----------------

        [Test]
        public void AQueuedMessage_IsPending()
        {
            _store.Enqueue(OutboxMessage.Admins, "المخزون منخفض", Now);

            OutboxMessage message = Only();
            Assert.That(message.Body, Is.EqualTo("المخزون منخفض"));
            Assert.That(message.Audience, Is.EqualTo(OutboxMessage.Admins));
            Assert.That(message.Attempts, Is.Zero);
            Assert.That(message.LastError, Is.Null);
        }

        [Test]
        public void AnEmptyBody_IsNotQueued()
        {
            _store.Enqueue(OutboxMessage.Admins, "", Now);
            _store.Enqueue(OutboxMessage.Admins, null, Now);
            _store.Enqueue(OutboxMessage.Admins, "   ", Now);

            Assert.That(_store.Pending(MaxAttempts, 50), Is.Empty,
                "a blank message would be delivered as a blank message");
        }

        [Test]
        public void AMissingAudience_BecomesABroadcast()
        {
            // Better than discarding it: something wanted to be said, and every manager hearing it is
            // a smaller mistake than nobody hearing it.
            _store.Enqueue(null, "شيء ما", Now);

            Assert.That(Only().Audience, Is.EqualTo(OutboxMessage.Admins));
        }

        [Test]
        public void MessagesComeOutOldestFirst()
        {
            _store.Enqueue(OutboxMessage.Admins, "first", Now);
            _store.Enqueue(OutboxMessage.Admins, "second", Now.AddMinutes(1));
            _store.Enqueue(OutboxMessage.Admins, "third", Now.AddMinutes(2));

            Assert.That(_store.Pending(MaxAttempts, 50).Select(m => m.Body),
                Is.EqualTo(new[] { "first", "second", "third" }));
        }

        [Test]
        public void TheBatchSizeIsRespected_SoALongBacklogDoesNotArriveAtOnce()
        {
            for (int i = 0; i < 30; i++)
                _store.Enqueue(OutboxMessage.Admins, "m" + i, Now.AddSeconds(i));

            Assert.That(_store.Pending(MaxAttempts, 10).Count, Is.EqualTo(10));
        }

        // ---------------- delivery ----------------

        [Test]
        public void ADeliveredMessage_IsNeverSelectedAgain()
        {
            _store.Enqueue(OutboxMessage.Admins, "once", Now);
            _store.MarkSent(Only().Id, Now);

            Assert.That(_store.Pending(MaxAttempts, 50), Is.Empty,
                "a manager must not receive the same alert twice because of a restart");
        }

        [Test]
        public void AFailedMessage_StaysPending_AndRemembersWhy()
        {
            _store.Enqueue(OutboxMessage.Admins, "retry me", Now);
            _store.MarkFailed(Only().Id, "network unreachable");

            OutboxMessage again = Only();
            Assert.That(again.Attempts, Is.EqualTo(1));
            Assert.That(again.LastError, Is.EqualTo("network unreachable"),
                "a stuck message has to be diagnosable, not merely late");
        }

        [Test]
        public void AMessageThatKeepsFailing_IsEventuallyLeftAlone()
        {
            _store.Enqueue(OutboxMessage.Admins, "poison", Now);
            int id = Only().Id;

            for (int i = 0; i < MaxAttempts; i++) _store.MarkFailed(id, "nope");

            Assert.That(_store.Pending(MaxAttempts, 50), Is.Empty,
                "without a cap this would be retried every five seconds for the life of the install");
        }

        [Test]
        public void AMessageLeftAlone_IsNotPretendedToHaveBeenSent()
        {
            _store.Enqueue(OutboxMessage.Admins, "poison", Now);
            int id = Only().Id;
            for (int i = 0; i < MaxAttempts; i++) _store.MarkFailed(id, "nope");

            (int pending, int stuck) = _store.OutboxHealth(MaxAttempts);

            Assert.That(pending, Is.Zero);
            Assert.That(stuck, Is.EqualTo(1),
                "it was NOT delivered; marking it sent would hide a real failure from the pharmacy");
        }

        [Test]
        public void ASuccessAfterFailures_ClearsTheError()
        {
            _store.Enqueue(OutboxMessage.Admins, "eventually", Now);
            int id = Only().Id;
            _store.MarkFailed(id, "transient");

            _store.MarkSent(id, Now);

            Assert.That(_store.Pending(MaxAttempts, 50), Is.Empty);
            Assert.That(_store.OutboxHealth(MaxAttempts), Is.EqualTo((0, 0)));
        }

        [Test]
        public void AVeryLongError_DoesNotBreakTheRow()
        {
            _store.Enqueue(OutboxMessage.Admins, "x", Now);
            Assert.DoesNotThrow(() => _store.MarkFailed(Only().Id, new string('e', 10_000)));
            Assert.That(Only().Attempts, Is.EqualTo(1));
        }

        // ---------------- who it is for ----------------

        [Test]
        public void ABroadcast_IsNotAddressedToOnePerson()
        {
            _store.Enqueue(OutboxMessage.Admins, "everyone", Now);

            Assert.That(Only().TryGetSingleRecipient(out _), Is.False);
        }

        [Test]
        public void AMessageForOnePerson_CarriesTheirId()
        {
            _store.Enqueue("111222333", "just you", Now);

            Assert.That(Only().TryGetSingleRecipient(out long id), Is.True);
            Assert.That(id, Is.EqualTo(111222333L));
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("not-a-number")]
        public void AnAudienceThatIsNotAUsableId_IsNotTreatedAsOnePerson(string audience)
        {
            // Zero is what an unset field looks like. Treating it as a recipient id would silently
            // send nothing; treating it as a broadcast at least reaches somebody.
            _store.Enqueue(audience, "who?", Now);

            Assert.That(Only().TryGetSingleRecipient(out _), Is.False);
        }

        // ---------------- remembering what was already said ----------------

        [Test]
        public void AMark_SurvivesAndCanBeOverwritten()
        {
            // How the daily digest avoids re-announcing itself every time the PC reboots.
            Assert.That(_store.ReadMark("low_stock_digest"), Is.Null);

            _store.WriteMark("low_stock_digest", "2026-09-26");
            Assert.That(_store.ReadMark("low_stock_digest"), Is.EqualTo("2026-09-26"));

            _store.WriteMark("low_stock_digest", "2026-09-27");
            Assert.That(_store.ReadMark("low_stock_digest"), Is.EqualTo("2026-09-27"));
        }

        [Test]
        public void AMark_DoesNotCollideWithTheToken()
        {
            _store.SaveToken("8012345678:AAH-x");
            _store.WriteMark("low_stock_digest", "2026-09-26");

            Assert.That(_store.ReadToken(), Is.EqualTo("8012345678:AAH-x"));
            Assert.That(_store.ReadMark("low_stock_digest"), Is.EqualTo("2026-09-26"));
        }
    }
}
