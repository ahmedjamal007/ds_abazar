using System;
using System.IO;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// Minimal production logger: appends to %LocalAppData%\Dawaii\logs\app.log (rolls at ~1 MB).
    /// Used for unhandled exceptions and startup failures so field problems are diagnosable.
    /// Never throws — logging must not take the app down.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();

        private static string LogFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dawaii", "logs", "app.log");

        public static void Error(string context, Exception ex)
            => Write("ERROR", context + (ex == null ? "" : Environment.NewLine + ex));

        public static void Info(string message) => Write("INFO", message);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string file = LogFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));

                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 1_000_000)
                    {
                        string old = file + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }

                    File.AppendAllText(file,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch { /* never let logging fail the app */ }
        }
    }
}
