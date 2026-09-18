using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using MySql.Data.MySqlClient;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Database backup/restore (FR-BAK-*). For the local SQLite backend a backup is a consistent copy
    /// of the file (SQLite online-backup API, safe while running); for the MySQL network backend it is
    /// a mysqldump. Either way the last N copies are kept in the configured folder (e.g. a USB drive)
    /// and the home screen warns if none succeeded in 3 days.
    /// </summary>
    public class BackupService
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ISettingsRepository _settings;
        private readonly IBackupRepository _backups;

        public BackupService(IDbConnectionFactory factory, ISettingsRepository settings, IBackupRepository backups)
        {
            _factory = factory;
            _settings = settings;
            _backups = backups;
        }

        private bool IsMySql => _factory.Kind == DbKind.MySql;
        private string Ext => IsMySql ? ".sql" : ".db";

        /// <summary>
        /// The extension this backend's backups are written with — ".db" for a local SQLite copy,
        /// ".sql" for a MySQL dump.
        ///
        /// Exposed because the restore dialog has to filter on it. It used to hard-code "*.sql", which is
        /// the MySQL answer, so on a default local install the file picker showed an empty folder and the
        /// admin could not reach their own backups at the one moment backups exist for.
        /// </summary>
        public string BackupExtension => Ext;

        /// <summary>True if no successful backup exists in the last 3 days (FR-BAK-03).</summary>
        public bool NeedsWarning()
            => BackupPolicy.NeedsWarning(_backups.GetLatestSuccess()?.CreatedAt, DateTime.Now);

        public BackupRecord LastSuccess() => _backups.GetLatestSuccess();

        /// <summary>Runs a backup if none succeeded today and a folder is configured (FR-BAK-01). Never throws.</summary>
        public bool RunDailyIfDue()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.Get("backup_folder"))) return false;
                BackupRecord last = _backups.GetLatestSuccess();
                if (last != null && last.CreatedAt.Date >= DateTime.Now.Date) return false;
                CreateBackup();
                return true;
            }
            catch { return false; }
        }

        public string CreateBackup()
        {
            string folder = _settings.Get("backup_folder");
            if (string.IsNullOrWhiteSpace(folder))
                throw new ValidationException("لم يتم تحديد مجلد النسخ الاحتياطي في الإعدادات.");
            Directory.CreateDirectory(folder);

            string name = BaseName();
            string file = Path.Combine(folder, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{Ext}");

            try
            {
                if (IsMySql) DumpMySql(file);
                else BackupSqlite(file);
            }
            catch (Exception ex)
            {
                Record(file, BackupStatus.Failed);
                throw new DomainException("فشل النسخ الاحتياطي: " + ex.Message);
            }

            Record(file, BackupStatus.Success, new FileInfo(file).Length);
            Prune(folder, name);
            return file;
        }

        /// <summary>Restores the database from a backup file (Admin only, FR-BAK-02). Restart the app afterwards.</summary>
        public void Restore(User user, string file)
        {
            Guard.RequireAdmin(user, "الاستعادة متاحة للمدير فقط.");
            if (!File.Exists(file)) throw new ValidationException("ملف النسخة غير موجود.");
            if (IsMySql) RestoreMySql(file);
            else RestoreSqlite(file);
        }

        // ---------------- SQLite ----------------

        private void BackupSqlite(string file)
        {
            using (var source = (SQLiteConnection)_factory.OpenConnection())
            using (var dest = new SQLiteConnection($"Data Source={file};Version=3;"))
            {
                dest.Open();
                source.BackupDatabase(dest, "main", "main", -1, null, 0);
            }
        }

        private void RestoreSqlite(string file)
        {
            using (var fs = File.OpenRead(file))
            {
                var header = new byte[16];
                if (fs.Read(header, 0, 16) < 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                    throw new ValidationException("الملف المختار ليس نسخة قاعدة بيانات صالحة.");
            }

            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            string live = _factory.SqliteFilePath;
            string safety = live + ".before-restore";
            File.Copy(live, safety, overwrite: true);
            try
            {
                TryDelete(live + "-wal");
                TryDelete(live + "-shm");
                File.Copy(file, live, overwrite: true);
            }
            catch (Exception ex)
            {
                File.Copy(safety, live, overwrite: true);
                throw new DomainException("فشلت الاستعادة: " + ex.Message);
            }
        }

        // ---------------- MySQL ----------------

        private void DumpMySql(string file)
        {
            var cs = new MySqlConnectionStringBuilder(_factory.ConnectionString);
            var psi = ToolProcess("mysqldump",
                $"--host={cs.Server} --port={cs.Port} --user={cs.UserID} --single-transaction " +
                $"--default-character-set=utf8mb4 {cs.Database}", cs.Password);
            psi.RedirectStandardOutput = true;

            using (var p = Process.Start(psi))
            using (var outFile = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                p.StandardOutput.BaseStream.CopyTo(outFile);
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new DomainException("mysqldump: " + err);
            }
        }

        private void RestoreMySql(string file)
        {
            var cs = new MySqlConnectionStringBuilder(_factory.ConnectionString);
            var psi = ToolProcess("mysql",
                $"--host={cs.Server} --port={cs.Port} --user={cs.UserID} --default-character-set=utf8mb4 {cs.Database}",
                cs.Password);
            psi.RedirectStandardInput = true;

            using (var p = Process.Start(psi))
            using (var input = File.OpenRead(file))
            {
                input.CopyTo(p.StandardInput.BaseStream);
                p.StandardInput.Close();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new DomainException("فشلت الاستعادة: " + err);
            }
        }

        private ProcessStartInfo ToolProcess(string tool, string args, string password)
        {
            string bin = _settings.Get("mysql_bin");
            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(bin) ? tool : Path.Combine(bin, tool + ".exe"),
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["MYSQL_PWD"] = password; // keep the password off the command line
            return psi;
        }

        // ---------------- shared ----------------

        private string BaseName()
            => IsMySql
                ? (new MySqlConnectionStringBuilder(_factory.ConnectionString).Database ?? "dawaii")
                : Path.GetFileNameWithoutExtension(_factory.SqliteFilePath);

        private void Prune(string folder, string baseName)
        {
            int keep = int.TryParse(_settings.Get("backup_keep_last"), out int n) ? n : 14;
            var files = Directory.GetFiles(folder, baseName + "_*" + Ext);
            foreach (string f in BackupPolicy.ToPrune(files, File.GetLastWriteTime, keep))
                try { File.Delete(f); } catch { /* best effort */ }
        }

        private void Record(string file, BackupStatus status, long size = 0)
            => _backups.Add(new BackupRecord { FilePath = file, SizeBytes = size, Status = status, CreatedAt = DateTime.Now });

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
