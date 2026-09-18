using System;
using System.Security.Cryptography;

namespace Dawaii.Core.Security
{
    /// <summary>
    /// PBKDF2 password hashing (NFR-04, DECISIONS D-03).
    /// Format: <c>v1$iterations$base64salt$base64hash</c> (SHA-256, 16-byte salt, 32-byte key).
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int DefaultIterations = 100_000;
        private const string Prefix = "v1";

        public static string Hash(string password, int iterations = DefaultIterations)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            var salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            byte[] hash = Derive(password, salt, iterations, HashSize);
            return string.Join("$", Prefix, iterations.ToString(),
                Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        /// <summary>Constant-time verification of a password against a stored hash.</summary>
        public static bool Verify(string password, string stored)
        {
            if (password == null || string.IsNullOrEmpty(stored)) return false;

            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Prefix) return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations <= 0) return false;

            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            byte[] actual = Derive(password, salt, iterations, expected.Length);
            return FixedTimeEquals(actual, expected);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations, int length)
        {
            // Rfc2898DeriveBytes with an explicit SHA-256 HMAC is available on .NET Framework 4.8.
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return pbkdf2.GetBytes(length);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
