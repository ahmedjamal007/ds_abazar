using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The nightly backup, and saying so when it stops working (V2.4).
    ///
    /// Backups are a pharmacy's only protection against losing the lot, and this one runs on a
    /// background thread at startup where nobody is watching. It swallowed every reason it failed and
    /// returned false; the caller wrapped it in a catch that could therefore never fire, under a
    /// comment claiming failures were recorded. They were not. A backup that had quietly stopped —
    /// the USB drive somebody unplugged, the folder somebody moved — left nothing at all to read.
    ///
    /// The three-day staleness warning still tells the pharmacist their backups are old. What these
    /// cover is the part that was missing: why.
    /// </summary>
    [TestFixture]
    public class DailyBackupTests
    {
        private sealed class FakeBackupRepository : IBackupRepository
        {
            public readonly List<BackupRecord> Records = new List<BackupRecord>();
            public void Add(BackupRecord record) => Records.Add(record);
            public BackupRecord GetLatestSuccess()
                => Records.Where(r => r.Status == BackupStatus.Success)
                          .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            public IReadOnlyList<BackupRecord> GetRecent(int limit) => Records.Take(limit).ToList();
        }

        private string _dbPath, _scratch;
        private SqliteConnectionFactory _db;
        private FakeSettingsRepository _settings;
        private FakeBackupRepository _backups;
        private BackupService _service;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "dawaii_bak_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);

            _dbPath = Path.Combine(_scratch, "dawaii.db");
            _db = new SqliteConnectionFactory(_dbPath);
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            _settings = new FakeSettingsRepository();
            _backups = new FakeBackupRepository();
            _service = new BackupService(_db, _settings, _backups);
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(_scratch, recursive: true); } catch { }
        }

        // ---------------- not running is usually not a failure ----------------

        [Test]
        public void WithNoBackupFolderConfigured_NothingHappensAndNothingIsReported()
        {
            Exception failure;
            bool ran = _service.RunDailyIfDue(out failure);

            Assert.That(ran, Is.False);
            Assert.That(failure, Is.Null,
                "a pharmacy that has never set a folder is not a pharmacy with a broken backup — " +
                "logging this nightly would bury the real thing among a year of noise");
        }

        [Test]
        public void WhenOneWasAlreadyTakenToday_NothingHappensAndNothingIsReported()
        {
            _settings.Seed("backup_folder", Path.Combine(_scratch, "out"));
            _backups.Add(new BackupRecord
            {
                FilePath = "already.db", Status = BackupStatus.Success, CreatedAt = DateTime.Now
            });

            Exception failure;
            bool ran = _service.RunDailyIfDue(out failure);

            Assert.That(ran, Is.False, "one a day is the rule");
            Assert.That(failure, Is.Null);
        }

        [Test]
        public void AnOlderBackup_DoesNotCountAsTodays()
        {
            _settings.Seed("backup_folder", Path.Combine(_scratch, "out"));
            _backups.Add(new BackupRecord
            {
                FilePath = "old.db", Status = BackupStatus.Success, CreatedAt = DateTime.Now.AddDays(-1)
            });

            Exception failure;
            Assert.That(_service.RunDailyIfDue(out failure), Is.True);
            Assert.That(failure, Is.Null);
        }

        // ---------------- a real failure says why ----------------

        [Test]
        public void WhenTheBackupCannotBeWritten_TheReasonComesBack()
        {
            // A folder that cannot exist: its parent is a file. This is what an unplugged drive or a
            // moved folder looks like from here — the attempt throws on the way to the disk.
            string blocker = Path.Combine(_scratch, "not-a-folder.txt");
            File.WriteAllText(blocker, "x");
            _settings.Seed("backup_folder", Path.Combine(blocker, "backups"));

            Exception failure;
            bool ran = _service.RunDailyIfDue(out failure);

            Assert.That(ran, Is.False);
            Assert.That(failure, Is.Not.Null,
                "this is the one the pharmacist needs, and it used to be thrown away");
            Assert.That(failure.Message, Is.Not.Empty);
        }

        [Test]
        public void AFailedAttempt_StillDoesNotThrow()
        {
            string blocker = Path.Combine(_scratch, "not-a-folder.txt");
            File.WriteAllText(blocker, "x");
            _settings.Seed("backup_folder", Path.Combine(blocker, "backups"));

            // It runs on a background thread while the login screen comes up. Letting this escape
            // would take the program down before the pharmacist had signed in.
            Exception failure;
            Assert.DoesNotThrow(() => _service.RunDailyIfDue(out failure));
            Assert.DoesNotThrow(() => _service.RunDailyIfDue());
        }

        // ---------------- and a good one still works ----------------

        [Test]
        public void ASuccessfulBackup_WritesAFileAndReportsNoFailure()
        {
            string folder = Path.Combine(_scratch, "out");
            _settings.Seed("backup_folder", folder);

            Exception failure;
            bool ran = _service.RunDailyIfDue(out failure);

            Assert.That(ran, Is.True);
            Assert.That(failure, Is.Null);

            var written = Directory.GetFiles(folder, "*.db");
            Assert.That(written.Length, Is.EqualTo(1), "a copy of the database is on the drive");
            Assert.That(new FileInfo(written[0]).Length, Is.GreaterThan(0));

            Assert.That(_backups.GetLatestSuccess(), Is.Not.Null,
                "and it is on the record, so the staleness warning knows it happened");
        }

        [Test]
        public void TheOverloadWithoutAReason_StillBehavesTheSame()
        {
            _settings.Seed("backup_folder", Path.Combine(_scratch, "out"));

            Assert.That(_service.RunDailyIfDue(), Is.True);
            Assert.That(_service.RunDailyIfDue(), Is.False, "the second call is today's second — skipped");
        }
    }
}
