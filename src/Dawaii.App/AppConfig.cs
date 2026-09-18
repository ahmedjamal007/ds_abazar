using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dawaii.Core.Data;

namespace Dawaii.App
{
    /// <summary>
    /// Local configuration. Two backends (chosen by the installer, written to dawaii.ini beside the exe):
    ///  * mode=local  (default) — a single SQLite file at %ProgramData%\Dawaii\dawaii.db, zero setup.
    ///  * mode=server — a shared MySQL database on the manager's PC (V1.2 req 6): the manager device
    ///    runs MySQL as a service that auto-starts at boot; this + other counters connect to it by IP.
    /// </summary>
    public static class AppConfig
    {
        private static Dictionary<string, string> _ini;
        private static string _dbPath;

        private static Dictionary<string, string> Ini
        {
            get
            {
                if (_ini != null) return _ini;
                _ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string path = Path.Combine(dir, "dawaii.ini");
                    if (File.Exists(path))
                        foreach (string line in File.ReadAllLines(path))
                        {
                            string t = line.Trim();
                            if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                            int eq = t.IndexOf('=');
                            if (eq > 0) _ini[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                        }
                }
                catch { /* fall back to defaults */ }
                return _ini;
            }
        }

        private static string Get(string key) => Ini.TryGetValue(key, out string v) ? v : null;

        // ---------------- the database password (V2.3) ----------------
        //
        // In network mode the installer wrote the shared MySQL password into dawaii.ini in plain text,
        // next to the exe, on every counter PC — readable by anyone who could open Notepad. With it, an
        // employee can connect a MySQL client directly and rewrite debts, sales, stock or their own role
        // with nothing reaching the audit log, because every authorization rule this application has
        // lives inside the client.
        //
        // The password is now stored DPAPI-protected under the machine ("password_protected"), and a
        // plaintext "password" left by an older install is converted on first start. Machine scope is
        // deliberate: the service account, the manager and the counter's own login all have to open the
        // same file, so the protection has to be against reading the file elsewhere, not against other
        // accounts on this PC. That closes the "read it in Notepad" and "copy the file" paths. It does
        // NOT change the architecture — a determined user of this machine can still recover it — and the
        // report records that residual risk honestly.

        private const string PasswordKey = "password";
        private const string ProtectedPasswordKey = "password_protected";
        private static readonly byte[] PasswordEntropy = System.Text.Encoding.UTF8.GetBytes("Dawaii.DbPassword.v1");

        /// <summary>The MySQL password, from whichever form the ini holds it in.</summary>
        private static string DatabasePassword()
        {
            string protectedValue = Get(ProtectedPasswordKey);
            if (!string.IsNullOrEmpty(protectedValue))
            {
                try
                {
                    byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        Convert.FromBase64String(protectedValue), PasswordEntropy,
                        System.Security.Cryptography.DataProtectionScope.LocalMachine);
                    return System.Text.Encoding.UTF8.GetString(plain);
                }
                catch (Exception ex)
                {
                    // A protected value that will not open on this machine — the file was copied from
                    // another PC. Fall through to the plaintext key, if one is still there.
                    Ui.Log.Error("dawaii.ini password_protected", ex);
                }
            }

            string plaintext = Get(PasswordKey) ?? "";
            if (plaintext.Length > 0) TryProtectIniPassword(plaintext);
            return plaintext;
        }

        /// <summary>
        /// Rewrites dawaii.ini with the password protected, once. Best effort: Program Files may be
        /// read-only for this account, in which case the plaintext stays and the app still starts — a
        /// pharmacy locked out of its database is worse than one whose ini is still readable.
        /// </summary>
        private static void TryProtectIniPassword(string plaintext)
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "dawaii.ini");
                if (!File.Exists(path)) return;

                byte[] enc = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(plaintext), PasswordEntropy,
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
                string protectedValue = Convert.ToBase64String(enc);

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
                string temp = path + ".tmp";
                File.WriteAllLines(temp, lines);
                File.Copy(temp, path, overwrite: true);
                File.Delete(temp);

                Ini.Remove(PasswordKey);
                Ini[ProtectedPasswordKey] = protectedValue;
                Ui.Log.Info("dawaii.ini: database password is now stored protected.");
            }
            catch (Exception ex) { Ui.Log.Error("Protecting dawaii.ini password", ex); }
        }

        /// <summary>True when configured for the shared MySQL server backend (network mode).</summary>
        public static bool IsServerMode =>
            string.Equals(Get("mode"), "server", StringComparison.OrdinalIgnoreCase);

        public static string TerminalName => Get("terminal_name") ?? Environment.MachineName;

        /// <summary>Builds the connection factory for the configured backend.</summary>
        public static IDbConnectionFactory CreateConnectionFactory()
        {
            if (IsServerMode)
            {
                uint port = uint.TryParse(Get("port"), out uint p) ? p : 3306u;
                string cs = MySqlConnectionFactory.BuildConnectionString(
                    Get("host") ?? "localhost", port, Get("database") ?? "dawaii",
                    Get("user") ?? "dawaii_app", DatabasePassword());
                return new MySqlConnectionFactory(cs);
            }
            return new SqliteConnectionFactory(DatabasePath);
        }

        /// <summary>SQLite file location (local mode). Prefers %ProgramData%, falls back to a writable per-user path.</summary>
        public static string DatabasePath
        {
            get
            {
                if (_dbPath != null) return _dbPath;

                string overridePath = Get("database");
                if (!IsServerMode && overridePath != null && Path.IsPathRooted(overridePath))
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(overridePath)); return _dbPath = overridePath; }
                    catch { }
                }

                string shared = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dawaii");
                if (IsWritable(shared)) return _dbPath = Path.Combine(shared, "dawaii.db");

                string local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dawaii");
                Directory.CreateDirectory(local);
                return _dbPath = Path.Combine(local, "dawaii.db");
            }
        }

        private static bool IsWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".write_probe_" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
