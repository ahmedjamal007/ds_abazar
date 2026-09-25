using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Where a Dawaii installation keeps its database, and how to connect to it (V2.6).
    ///
    /// This used to live in the WinForms project, which was fine while the desktop application was
    /// the only thing that opened the database. The Telegram bot is a second process — a headless
    /// Windows Service — and it cannot reference a WinExe carrying WinForms and WPF just to find out
    /// where the data is. So the part that resolves a connection moved here, to the assembly both
    /// sides already reference, and <c>Dawaii.App.AppConfig</c> now forwards to it.
    ///
    /// Deliberately an INSTANCE rather than the static it replaced. The desktop app reads
    /// <c>dawaii.ini</c> from beside its own executable; the bot service runs from a different folder
    /// entirely and has to be pointed at the pharmacy's installation. A static tied to the executing
    /// assembly's location silently gave the bot the wrong answer — an empty configuration, which
    /// looks exactly like a default local install and would have had it open a NEW, blank database
    /// instead of the pharmacy's.
    ///
    /// Two backends, chosen by the installer and written into <c>dawaii.ini</c>:
    ///   * <c>mode=local</c> (default) — one SQLite file, zero setup.
    ///   * <c>mode=server</c> — a shared MySQL database on the manager's PC; counters connect by IP.
    /// </summary>
    public sealed class DawaiiInstallation
    {
        private readonly Dictionary<string, string> _ini;
        private readonly string _directory;
        private string _databasePath;

        private DawaiiInstallation(string directory, Dictionary<string, string> ini)
        {
            _directory = directory ?? "";
            _ini = ini;
        }

        /// <summary>The folder this configuration was read from. Empty when nothing was found.</summary>
        public string Directory => _directory;

        /// <summary>True when a <c>dawaii.ini</c> was actually found and read.</summary>
        public bool Found { get; private set; }

        /// <summary>
        /// Reads <c>dawaii.ini</c> from a specific folder.
        ///
        /// A missing or unreadable file is not an error: it means a default local install, which is
        /// the overwhelmingly common case and needs no file at all. <see cref="Found"/> reports which
        /// happened, for a caller that cares — the bot does, because for it an absent file means it
        /// was pointed somewhere wrong.
        /// </summary>
        public static DawaiiInstallation FromDirectory(string directory)
        {
            var ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var config = new DawaiiInstallation(directory, ini);

            try
            {
                if (string.IsNullOrWhiteSpace(directory)) return config;

                string path = Path.Combine(directory, "dawaii.ini");
                if (!File.Exists(path)) return config;

                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0) ini[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
                config.Found = true;
            }
            catch
            {
                // An unreadable ini falls back to defaults rather than refusing to start. Core cannot
                // log — the caller decides whether silence is acceptable.
            }
            return config;
        }

        /// <summary>
        /// Reads the configuration belonging to the currently running program — beside its own
        /// executable. Correct for the desktop app; the bot service must use
        /// <see cref="FromDirectory"/> with the pharmacy's install folder instead.
        /// </summary>
        public static DawaiiInstallation ForThisProgram()
        {
            string directory = "";
            try
            {
                Assembly entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                directory = Path.GetDirectoryName(entry.Location) ?? "";
            }
            catch { }
            return FromDirectory(directory);
        }

        private string Get(string key) => _ini.TryGetValue(key, out string v) ? v : null;

        /// <summary>True when configured for the shared MySQL backend (network mode).</summary>
        public bool IsServerMode =>
            string.Equals(Get("mode"), "server", StringComparison.OrdinalIgnoreCase);

        public string TerminalName => Get("terminal_name") ?? Environment.MachineName;

        /// <summary>Builds the connection factory for the configured backend.</summary>
        public IDbConnectionFactory CreateConnectionFactory()
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

        /// <summary>
        /// The SQLite file (local mode). Prefers <c>%ProgramData%</c> so every account on the PC — and
        /// a Windows Service — opens the same file; falls back to a per-user path only when that is
        /// not writable.
        /// </summary>
        public string DatabasePath
        {
            get
            {
                if (_databasePath != null) return _databasePath;

                string overridePath = Get("database");
                if (!IsServerMode && overridePath != null && Path.IsPathRooted(overridePath))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
                        return _databasePath = overridePath;
                    }
                    catch { }
                }

                string shared = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dawaii");
                if (IsWritable(shared)) return _databasePath = Path.Combine(shared, "dawaii.db");

                string local = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dawaii");
                System.IO.Directory.CreateDirectory(local);
                return _databasePath = Path.Combine(local, "dawaii.db");
            }
        }

        /// <summary>
        /// The MySQL password for network mode.
        ///
        /// Stored DPAPI-protected under the MACHINE, not the user. That is deliberate and predates the
        /// bot: the manager's login, a counter's own login and a Windows Service all have to open the
        /// same file, so the protection is against the file being read elsewhere rather than against
        /// other accounts on this PC. A plaintext <c>password</c> left by an older install is still
        /// accepted, so an un-migrated counter keeps working.
        /// </summary>
        public string DatabasePassword()
        {
            string protectedValue = Get("password_protected");
            if (!string.IsNullOrWhiteSpace(protectedValue))
            {
                // The SAME purpose the desktop app has used since V2.3. Getting this wrong would mean
                // every networked pharmacy silently losing the ability to decrypt its own password.
                string unprotected = MachineSecret.Unprotect(
                    protectedValue, MachineSecret.DatabasePasswordPurpose);
                if (unprotected != null) return unprotected;
                // Undecryptable: the file was copied from another machine. Fall through to plaintext
                // if one is present, rather than failing to connect at all.
            }
            return Get("password") ?? "";
        }

        /// <summary>True when the ini still holds a plaintext password that ought to be protected.</summary>
        public bool HasPlaintextPassword =>
            !string.IsNullOrWhiteSpace(Get("password")) &&
            string.IsNullOrWhiteSpace(Get("password_protected"));

        private static bool IsWritable(string folder)
        {
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".write_probe_" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
