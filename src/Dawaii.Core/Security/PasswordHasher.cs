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

        /// <summary>
        /// PBKDF2-HMAC-SHA256. Both branches below compute the same standard and must produce byte-
        /// for-byte identical output: a stored hash was derived by whichever branch was compiled at
        /// the time, and every password in every pharmacy already on this software depends on the
        /// other one agreeing with it. PasswordHasherTests pins that against an independent
        /// implementation rather than against ourselves, which is the only way the agreement can
        /// actually be checked.
        /// </summary>
        private static byte[] Derive(string password, byte[] salt, int iterations, int length)
        {
#if NETFRAMEWORK
            // The constructor is the only route on .NET Framework, where the static below does not exist.
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return pbkdf2.GetBytes(length);
#else
            // On modern .NET the constructors are obsolete (SYSLIB0060) and this is the sanctioned
            // route. Same algorithm, same parameters, same bytes.
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
#endif
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
