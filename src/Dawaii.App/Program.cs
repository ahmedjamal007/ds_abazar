using System;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;

namespace Dawaii.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // The message alone used to be all that survived an unexpected failure: no type, no stack,
            // nothing in app.log. A pharmacist would report "it crashed" and there was nothing to read.
            // Log first, then tell the user — and cover the non-UI threads too, which had no handler at
            // all and so could take the process down in silence.
            Application.ThreadException += (s, e) =>
            {
                Log.Error("UI thread", e.Exception);
                Msg.Error("حدث خطأ غير متوقع:\n" + e.Exception.Message);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error("Unhandled (" + (e.IsTerminating ? "terminating" : "non-terminating") + ")",
                    e.ExceptionObject as Exception);

            var services = new AppServices(AppConfig.CreateConnectionFactory());
            Session.Services = services;

            // Local mode: first run creates the SQLite file. Server mode: the MySQL service on the
            // manager PC is already running at boot; we just apply the (idempotent) schema/seed.
            try
            {
                services.Initializer.ApplySchemaAndSeed();
                services.Initializer.EnsureDefaultAdmin();
            }
            catch (Exception ex)
            {
                Log.Error("DB init", ex);
                string hint = AppConfig.IsServerMode
                    ? "تعذّر الاتصال بقاعدة بيانات المدير (MySQL).\n" +
                      "تأكد أن جهاز المدير يعمل وأن الشبكة متصلة، ثم أعد المحاولة."
                    : "تعذّر تجهيز قاعدة البيانات المحلية.\n" +
                      "المسار: " + AppConfig.DatabasePath + "\n" +
                      "تأكد أن المجلد قابل للكتابة، أو احذف dawaii.db إذا كان تالفاً.";
                Msg.Error(hint + "\n\nالسبب: " + ex.Message);
                return;
            }

            // Automatic daily backup (FR-BAK-01) — best effort, never blocks startup.
            System.Threading.Tasks.Task.Run(() =>
            {
                // RunDailyIfDue never throws, so a catch around it is dead code — which is exactly
                // what used to be here, under a comment claiming failures were recorded. They were
                // not. The reason comes back instead, and only a real failure is worth writing down:
                // "no folder configured" and "already done today" are both ordinary.
                Exception failure;
                services.Backup.RunDailyIfDue(out failure);
                if (failure != null) Log.Error("Daily backup", failure);
            });

            // Login → Main loop. Logging out returns to the login screen.
            while (true)
            {
                using (var login = new LoginForm(services))
                {
                    if (login.ShowDialog() != DialogResult.OK)
                        return; // user closed the login window

                    // V1.2 req 2: attendance — record the clock-in automatically at login.
                    //
                    // Deliberately non-fatal: a pharmacist whose clock-in will not save must still be
                    // able to open the till and serve the person in front of them. But it is no longer
                    // silent. This swallowed everything, so attendance could stop recording for weeks
                    // — payroll is worked out from these rows — and there was nothing to read.
                    try { services.Employees.RecordLogin(Session.CurrentUser, AppConfig.TerminalName); }
                    catch (Exception ex) { Log.Error("Attendance clock-in", ex); }

                    using (var main = new MainForm())
                    {
                        DialogResult result = main.ShowDialog();
                        if (result != DialogResult.Retry)
                            return; // closed / exit (Retry means "log out and show login again")
                    }
                }
            }
        }
    }
}
