using System;
using System.Text;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Small secrets kept on one machine: the MySQL password, and from V2.6 the Telegram bot token.
    ///
    /// DPAPI under the MACHINE scope, not the user. Every account that has to read these — the
    /// manager's login, a counter's own login, and a Windows Service running as LocalSystem — is a
    /// different account on the same PC, so user-scoped protection would mean whichever account wrote
    /// the value being the only one able to read it back. That is precisely the trap a service falls
    /// into, and it fails at a pharmacy rather than on a developer's desk.
    ///
    /// Be clear about what this does and does not buy. It stops the secret being read out of a file
    /// in Notepad, and it stops the file being useful if copied to another machine. It does NOT stop
    /// someone who can already run code as an administrator on this PC — they can call Unprotect just
    /// as this does. For a pharmacy's own till that is the right trade; for a Telegram token the
    /// remedy if it leaks is to revoke it in BotFather, which is the only remedy there has ever been.
    ///
    /// Every secret is protected under a PURPOSE, which becomes DPAPI's optional entropy. Two reasons.
    /// The mundane one is that the database password has been stored with the purpose string below
    /// since V2.3, and a version of this class that ignored entropy could not decrypt it — every
    /// networked pharmacy would simply stop being able to connect, with no error that pointed at the
    /// cause. The better one is that a value protected for one purpose cannot then be read as
    /// another, so a bot token pasted into the password field is not silently usable as a password.
    ///
    /// Lives in Core, which has no logging, so every failure is reported by return value.
    /// </summary>
    public static class MachineSecret
    {
        /// <summary>
        /// The purpose the MySQL password has been protected under since V2.3. Must never change:
        /// every networked pharmacy has a dawaii.ini holding a value encrypted with exactly this.
        /// </summary>
        public const string DatabasePasswordPurpose = "Dawaii.DbPassword.v1";

        /// <summary>The purpose the Telegram bot token is protected under (V2.6).</summary>
        public const string BotTokenPurpose = "Dawaii.BotToken.v1";

        /// <summary>
        /// Encrypts a secret for this machine and returns it base64-encoded, or null if it could not
        /// be protected — any non-Windows host has no DPAPI. A null return means "do not store this";
        /// never fall back to writing it in the clear.
        /// </summary>
        public static string Protect(string plaintext, string purpose)
        {
            if (plaintext == null || string.IsNullOrEmpty(purpose)) return null;
            try
            {
                byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext), Entropy(purpose),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Decrypts a value produced by <see cref="Protect"/> on THIS machine under the SAME purpose,
        /// or null when it cannot be read: a corrupt value, a different purpose, or a configuration
        /// file copied from another PC. Null is an ordinary outcome here rather than an exception —
        /// people do copy an installation between machines, and it must not crash anything.
        /// </summary>
        public static string Unprotect(string protectedBase64, string purpose)
        {
            if (string.IsNullOrWhiteSpace(protectedBase64) || string.IsNullOrEmpty(purpose)) return null;
            try
            {
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedBase64.Trim()), Entropy(purpose),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes(purpose);
    }
}
