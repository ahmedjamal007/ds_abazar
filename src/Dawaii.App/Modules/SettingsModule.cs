using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;

namespace Dawaii.App.Modules
{
    /// <summary>Settings + backup/restore (FR-BAK-*, FR-NET config). Admin only.</summary>
    public class SettingsModule : ModuleControl
    {
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private Label _backupInfo;

        private static readonly (string key, string label)[] Keys =
        {
            ("pharmacy_name", "اسم الصيدلية"),
            ("currency", "العملة"),
            ("expiry_warn_days", "أيام تنبيه الصلاحية (تقرير)"),
            ("pos_expiry_warn_days", "أيام تنبيه الصلاحية (بيع)"),
            ("low_stock_default", "حد المخزون المنخفض الافتراضي"),
            ("backup_folder", "مجلد النسخ الاحتياطي"),
            ("backup_keep_last", "عدد النسخ المحفوظة"),
            ("price_rounding_step", "تقريب الأسعار لأقرب (0 = تلقائي حسب السعر)"),
            ("mysql_bin", "مسار أدوات MySQL (bin) — للنسخ في وضع الشبكة")
        };

        public SettingsModule()
        {
            var title = new Label { Text = "الإعدادات", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(8), RightToLeft = RightToLeft.Yes };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            foreach (var (key, label) in Keys)
            {
                table.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Right, AutoSize = true, Font = Theme.Base(10.5f), Margin = new Padding(6, 9, 6, 6) });
                var box = new TextBox { Dock = DockStyle.Fill, Font = Theme.Base(11f), Margin = new Padding(6, 5, 6, 5) };
                _fields[key] = box;

                if (key == "backup_folder")
                {
                    // Folder picker: the user browses to a directory instead of typing a path (V1.3).
                    var row = new Panel { Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0) };
                    var browse = new Button { Text = "استعراض…", Dock = DockStyle.Right, Width = 96, Font = Theme.Base(10f) };
                    Theme.StyleSecondaryButton(browse);
                    browse.Click += (s, e) => BrowseFolder(box);
                    box.Dock = DockStyle.Fill;
                    row.Controls.Add(box);
                    row.Controls.Add(browse);
                    table.Controls.Add(row);
                }
                else
                {
                    table.Controls.Add(box);
                }
            }

            var save = Btn("حفظ الإعدادات", SaveSettings, primary: true);
            var terminal = new Label
            {
                Text = $"قاعدة البيانات المحلية: {AppConfig.DatabasePath}",
                Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(10f)
            };

            var backupPanel = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Surface, Padding = new Padding(10) };
            var backupTitle = new Label { Text = "النسخ الاحتياطي", Font = Theme.Base(13f, FontStyle.Bold), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 28 };
            _backupInfo = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f) };
            var backupButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft };
            backupButtons.Controls.Add(Btn("نسخ احتياطي الآن", DoBackup, primary: true));
            backupButtons.Controls.Add(Btn("استعادة…", DoRestore));
            backupPanel.Controls.Add(backupButtons);
            backupPanel.Controls.Add(_backupInfo);
            backupPanel.Controls.Add(backupTitle);

            var host = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(backupPanel);
            host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 });
            host.Controls.Add(save);
            host.Controls.Add(terminal);
            host.Controls.Add(table);

            Controls.Add(host);
            Controls.Add(title);
        }

        public override void OnActivated()
        {
            foreach (var kv in _fields)
                kv.Value.Text = SafeGet(kv.Key);
            RefreshBackupInfo();
        }

        private void SaveSettings()
        {
            try
            {
                foreach (var kv in _fields)
                    Session.Services.Settings.Set(kv.Key, kv.Value.Text.Trim());
                Fmt.ResetCurrency();
                Msg.Info("تم حفظ الإعدادات.");
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void RefreshBackupInfo()
        {
            try
            {
                var last = Session.Services.Backup.LastSuccess();
                _backupInfo.Text = last == null
                    ? "لا توجد نسخة احتياطية بعد."
                    : $"آخر نسخة ناجحة: {Fmt.DateTime(last.CreatedAt)} — {last.FilePath}";
            }
            catch (Exception ex) { _backupInfo.Text = ex.Message; }
        }

        private void DoBackup()
        {
            try
            {
                string path = Session.Services.Backup.CreateBackup();
                Msg.Info("تم إنشاء نسخة احتياطية:\n" + path);
                RefreshBackupInfo();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر النسخ الاحتياطي: " + ex.Message); }
        }

        private void DoRestore()
        {
            // The filter follows the backend, because the two write different formats: a local install
            // makes .db copies, a server install makes .sql dumps. Hard-coding "*.sql" showed a local
            // pharmacy an empty folder and put its own backups out of reach.
            string ext = Session.Services.Backup.BackupExtension;
            string filter = "نسخة احتياطية (*" + ext + ")|*" + ext + "|كل الملفات (*.*)|*.*";

            using (var dlg = new OpenFileDialog { Filter = filter, Title = "اختر ملف النسخة الاحتياطية" })
            {
                dlg.InitialDirectory = SafeGet("backup_folder");
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (!Msg.Confirm("سيتم استبدال البيانات الحالية بالنسخة المختارة. متابعة؟")) return;
                try
                {
                    Session.Services.Backup.Restore(Session.CurrentUser, dlg.FileName);

                    // The restored database has its own users table, so the signed-in user is a stale
                    // reference to a row that may now be someone else or nobody. Restarting is not
                    // advice the user can decline — every write until then would be misattributed.
                    Msg.Info("تمت الاستعادة بنجاح. سيتم إعادة تشغيل البرنامج الآن.");
                    Application.Restart();
                }
                catch (DomainException ex) { Msg.Error(ex.Message); }
                catch (Exception ex) { Log.Error("Restore", ex); Msg.Error("تعذّرت الاستعادة: " + ex.Message); }
            }
        }

        private void BrowseFolder(TextBox target)
        {
            using (var dlg = new FolderBrowserDialog { Description = "اختر مجلد حفظ النسخ الاحتياطية", ShowNewFolderButton = true })
            {
                if (!string.IsNullOrWhiteSpace(target.Text) && System.IO.Directory.Exists(target.Text))
                    dlg.SelectedPath = target.Text;
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    target.Text = dlg.SelectedPath;
            }
        }

        private string SafeGet(string key) { try { return Session.Services.Settings.Get(key) ?? ""; } catch { return ""; } }

        private static Button Btn(string text, Action onClick, bool primary = false)
            => Theme.ActionButton(text, onClick, primary, width: primary ? 180 : 170);
    }
}
