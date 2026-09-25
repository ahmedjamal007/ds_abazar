using Dawaii.Core.Security;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class PasswordHasherTests
    {
        [Test]
        public void Verify_CorrectPassword_ReturnsTrue()
        {
            string hash = PasswordHasher.Hash("s3cret!");
            Assert.That(PasswordHasher.Verify("s3cret!", hash), Is.True);
        }

        [Test]
        public void Verify_WrongPassword_ReturnsFalse()
        {
            string hash = PasswordHasher.Hash("s3cret!");
            Assert.That(PasswordHasher.Verify("wrong", hash), Is.False);
        }

        [Test]
        public void Hash_IsSalted_ProducesDifferentHashesForSamePassword()
        {
            string a = PasswordHasher.Hash("same");
            string b = PasswordHasher.Hash("same");
            Assert.That(a, Is.Not.EqualTo(b), "Each hash must use a fresh random salt.");
            Assert.That(PasswordHasher.Verify("same", a), Is.True);
            Assert.That(PasswordHasher.Verify("same", b), Is.True);
        }

        [Test]
        public void Hash_HasExpectedFormat()
        {
            string hash = PasswordHasher.Hash("pw", 100000);
            string[] parts = hash.Split('$');
            Assert.That(parts.Length, Is.EqualTo(4));
            Assert.That(parts[0], Is.EqualTo("v1"));
            Assert.That(parts[1], Is.EqualTo("100000"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-valid-hash")]
        [TestCase("v1$abc$def")]
        public void Verify_MalformedStored_ReturnsFalse(string stored)
        {
            Assert.That(PasswordHasher.Verify("pw", stored), Is.False);
        }

        // ---------------- the stored hashes must keep working (V2.5) ----------------

        /// <summary>
        /// A hash computed OUTSIDE this codebase — Python's hashlib.pbkdf2_hmac, SHA-256, the salt
        /// 00 01 02 ... 0f, 100,000 iterations, 32-byte key — for the password below.
        ///
        /// Every other test here hashes and then verifies with the same code, so all of them would
        /// still pass if the derivation changed: the round trip would simply agree with itself. What
        /// would not survive is the password of every user in every pharmacy already running this,
        /// because their stored hash was produced by the old code and nothing would match it again.
        ///
        /// This one pins the actual bytes, and pins them against an independent implementation of the
        /// standard rather than against ourselves. It is the test that makes the move off .NET
        /// Framework safe to do here at all.
        /// </summary>
        private const string KnownGoodHash =
            "v1$100000$AAECAwQFBgcICQoLDA0ODw==$N2LEnTWddxL7FFkj8PhCmKnKhGAry4y4GX4OJ4uwoZs=";

        [Test]
        public void AHashFromAnIndependentPbkdf2_StillVerifies()
        {
            Assert.That(PasswordHasher.Verify("s3cret!", KnownGoodHash), Is.True,
                "if this fails, every password already stored in every pharmacy has stopped working");
        }

        [Test]
        public void AHashFromAnIndependentPbkdf2_RefusesTheWrongPassword()
        {
            Assert.That(PasswordHasher.Verify("s3cret", KnownGoodHash), Is.False);
            Assert.That(PasswordHasher.Verify("S3cret!", KnownGoodHash), Is.False);
            Assert.That(PasswordHasher.Verify("", KnownGoodHash), Is.False);
        }

        [Test]
        public void ThisCodesOwnDerivation_MatchesTheStandard()
        {
            // Same inputs as KnownGoodHash, hashed here, compared byte for byte with the reference.
            string[] reference = KnownGoodHash.Split('$');
            byte[] salt = System.Convert.FromBase64String(reference[2]);

            string mine = PasswordHasher.Hash("s3cret!", 100000);
            string[] parts = mine.Split('$');

            // Hash() salts randomly, so the comparison has to go through Verify, which uses the
            // stored salt — that is what proves the derivation itself agrees, not the formatting.
            Assert.That(parts[0], Is.EqualTo(reference[0]), "version prefix");
            Assert.That(parts[1], Is.EqualTo(reference[1]), "iteration count");
            Assert.That(System.Convert.FromBase64String(parts[2]).Length, Is.EqualTo(salt.Length), "salt size");
            Assert.That(System.Convert.FromBase64String(parts[3]).Length,
                Is.EqualTo(System.Convert.FromBase64String(reference[3]).Length), "key size");
            Assert.That(PasswordHasher.Verify("s3cret!", KnownGoodHash), Is.True, "and the bytes agree");
        }

    }
}
