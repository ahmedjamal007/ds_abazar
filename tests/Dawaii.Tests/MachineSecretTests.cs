using Dawaii.Core.Data;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Secrets kept on one machine (V2.6): the MySQL password, and now the Telegram bot token.
    ///
    /// This class was extracted out of the WinForms project so a headless Windows Service could read
    /// the same values. The extraction nearly broke every networked pharmacy: the original protected
    /// the password under a DPAPI entropy of "Dawaii.DbPassword.v1", and a version that passed no
    /// entropy cannot decrypt it. Nothing would have thrown at build time; the app would simply have
    /// failed to connect to MySQL, on customer machines, with an error pointing at the database
    /// rather than at the change.
    ///
    /// So the purpose string is pinned here as a literal, deliberately duplicated rather than read
    /// from the constant. A test that asserts a constant equals itself would let somebody rename the
    /// value and stay green.
    /// </summary>
    [TestFixture]
    public class MachineSecretTests
    {
        /// <summary>
        /// The exact entropy the desktop app has used since V2.3.
        ///
        /// If this test fails, the constant was changed, and every pharmacy in network mode is about
        /// to lose the ability to decrypt its own database password. It is not a style choice.
        /// </summary>
        [Test]
        public void TheDatabasePasswordPurpose_IsUnchangedSinceV23()
        {
            Assert.That(MachineSecret.DatabasePasswordPurpose, Is.EqualTo("Dawaii.DbPassword.v1"),
                "every networked pharmacy has a dawaii.ini encrypted with exactly this string");
        }

        [Test]
        public void ASecretRoundTripsOnThisMachine()
        {
            string sealed_ = MachineSecret.Protect("s3cret-password", MachineSecret.DatabasePasswordPurpose);

            Assert.That(sealed_, Is.Not.Null.And.Not.Empty);
            Assert.That(sealed_, Does.Not.Contain("s3cret"), "the stored form must not reveal the value");
            Assert.That(MachineSecret.Unprotect(sealed_, MachineSecret.DatabasePasswordPurpose),
                Is.EqualTo("s3cret-password"));
        }

        [Test]
        public void ASecretSealedForOnePurpose_CannotBeReadAsAnother()
        {
            // The reason purposes exist at all. A bot token pasted into the password field must not
            // then be usable as a database password, and vice versa.
            string token = MachineSecret.Protect("8012345678:AAH-fake", MachineSecret.BotTokenPurpose);

            Assert.That(MachineSecret.Unprotect(token, MachineSecret.BotTokenPurpose),
                Is.EqualTo("8012345678:AAH-fake"));
            Assert.That(MachineSecret.Unprotect(token, MachineSecret.DatabasePasswordPurpose), Is.Null,
                "the entropy is doing its job");
        }

        [Test]
        public void TheTwoPurposes_AreDifferentStrings()
        {
            Assert.That(MachineSecret.BotTokenPurpose,
                Is.Not.EqualTo(MachineSecret.DatabasePasswordPurpose));
        }

        // ---------------- what must never throw ----------------

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not-base64-at-all!!")]
        [TestCase("aGVsbG8=")]          // valid base64, not DPAPI output
        public void SomethingThatIsNotASealedSecret_ReturnsNull_RatherThanThrowing(string stored)
        {
            // A dawaii.ini copied from another machine holds a value this PC cannot open. That is an
            // ordinary thing people do when moving an installation, and it must not crash the app on
            // startup — the caller falls back to the plaintext key instead.
            Assert.That(MachineSecret.Unprotect(stored, MachineSecret.DatabasePasswordPurpose), Is.Null);
        }

        [Test]
        public void ProtectingNothing_IsRefused_NotStoredAsEmpty()
        {
            Assert.That(MachineSecret.Protect(null, MachineSecret.BotTokenPurpose), Is.Null);
            Assert.That(MachineSecret.Protect("anything", null), Is.Null);
            Assert.That(MachineSecret.Protect("anything", ""), Is.Null);
        }

        [Test]
        public void AnEmptySecret_IsStillSealable_BecauseABlankPasswordIsALegitimateSetting()
        {
            string sealed_ = MachineSecret.Protect("", MachineSecret.DatabasePasswordPurpose);

            Assert.That(sealed_, Is.Not.Null);
            Assert.That(MachineSecret.Unprotect(sealed_, MachineSecret.DatabasePasswordPurpose),
                Is.EqualTo(""));
        }
    }
}
