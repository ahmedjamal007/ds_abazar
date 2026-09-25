using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dawaii.Core.Data;

namespace Dawaii.App
{
    /// <summary>
    /// The desktop application's view of its own installation.
    ///
    /// The work moved to <see cref="DawaiiInstallation"/> in Dawaii.Core (V2.6), because the Telegram
    /// bot is a second process that needs the same answers and cannot reference a WinExe carrying
    /// WinForms and WPF to get them. What is left here is this: the static shape the app's call sites
    /// already use, and the one-time rewrite of a plaintext password left by an older install — which
    /// stays app-side because it logs, and Dawaii.Core has no logging.
    /// </summary>
    public static class AppConfig
    {
        private static DawaiiInstallation _installation;
        private static bool _passwordMigrationAttempted;

        /// <summary>Read from beside this executable, once.</summary>
        private static DawaiiInstallation Installation =>
            _installation ?? (_installation = DawaiiInstallation.ForThisProgram());

        /// <summary>True when configured for the shared MySQL server backend (network mode).</summary>
        public static bool IsServerMode => Installation.IsServerMode;

        public static string TerminalName => Installation.TerminalName;

        /// <summary>SQLite file location (local mode).</summary>
        public static string DatabasePath => Installation.DatabasePath;

        /// <summary>Builds the connection factory for the configured backend.</summary>
        public static IDbConnectionFactory CreateConnectionFactory()
        {
            // Best moment to upgrade an old install: before anything opens a connection, and only
            // when there is actually a plaintext password sitting in the file.
            if (!_passwordMigrationAttempted && Installation.HasPlaintextPassword)
            {
                _passwordMigrationAttempted = true;
                ProtectIniPassword();
                _installation = DawaiiInstallation.ForThisProgram();   // re-read what we just wrote
            }

            return Installation.CreateConnectionFactory();
        }

        // ---------------- upgrading an old install (V2.3) ----------------
        //
        // In network mode the installer wrote the shared MySQL password into dawaii.ini in plain text,
        // next to the exe, on every counter PC — readable by anyone who could open Notepad. With it an
        // employee can connect a MySQL client directly and rewrite debts, sales, stock or their own
        // role with nothing reaching the audit log, because every authorization rule this application
        // has lives inside the client.
        //
        // The password is stored DPAPI-protected under the MACHINE, and a plaintext value left by an
        // older install is converted here on first start. Machine scope is deliberate: the manager's
        // login, a counter's own login and the bot's Windows Service all have to open the same file,
        // so the protection is against the file being read elsewhere rather than against other
        // accounts on this PC. That closes "read it in Notepad" and "copy the file". It does NOT
        // change the architecture — a determined user of this machine can still recover it.

        private const string PasswordKey = "password";
        private const string ProtectedPasswordKey = "password_protected";

        /// <summary>
        /// Rewrites dawaii.ini with the password protected, once. Best effort: Program Files may be
        /// read-only for this account, in which case the plaintext stays and the app still starts — a
        /// pharmacy locked out of its database is worse than one whose ini is still readable.
        /// </summary>
        private static void ProtectIniPassword()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "dawaii.ini");
                if (!File.Exists(path)) return;

                string plaintext = ReadKey(path, PasswordKey);
                if (string.IsNullOrEmpty(plaintext)) return;

                string protectedValue = MachineSecret.Protect(
                    plaintext, MachineSecret.DatabasePasswordPurpose);
                if (protectedValue == null)
                {
                    // No DPAPI here. Leave the plaintext rather than destroying the only copy of a
                    // password the pharmacy needs to connect.
                    Ui.Log.Error("Protecting dawaii.ini password",
                        new InvalidOperationException("ProtectedData unavailable; password left as-is."));
                    return;
                }

                var lines = new List<string>();
                bool replaced = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    string key = eq > 0 ? t.Substring(0, eq).Trim() : null;

                    if (string.Equals(key, PasswordKey, StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add(ProtectedPasswordKey + "=" + protectedValue);
                        replaced = true;
                    }
                    else if (string.Equals(key, ProtectedPasswordKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // a stale protected line from a copied file
                    }
                    else lines.Add(line);
                }
                if (!replaced) return;

                // Atomic replace: the file is either the old one or the new one, never half of each.
                // A half-written ini is a pharmacy that cannot start.
                string temp = path + ".tmp";
                File.WriteAllLines(temp, lines);
                File.Copy(temp, path, overwrite: true);
                File.Delete(temp);

                Ui.Log.Info("dawaii.ini: database password is now stored protected.");
            }
            catch (Exception ex) { Ui.Log.Error("Protecting dawaii.ini password", ex); }
        }

        private static string ReadKey(string iniPath, string key)
        {
            foreach (string line in File.ReadAllLines(iniPath))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return t.Substring(eq + 1).Trim();
            }
            return null;
        }
    }
}
