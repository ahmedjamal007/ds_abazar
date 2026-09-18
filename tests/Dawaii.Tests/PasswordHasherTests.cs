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
    }
}
