using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Bot;
using Dawaii.Core.Data;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// إعداد تيليجرام — setting up the manager's Telegram bot (V2.6). Manager only.
    ///
    /// Three jobs, and they are the three things nobody should have to use a terminal for: put the
    /// bot's token in, hand a manager a code so their phone can be linked, and take that access away
    /// again. The bot itself is a Windows Service with no interface at all, so this screen is the
    /// only place any of it is visible.
    ///
    /// Writes to the BOT's database, never the pharmacy's. Everything here is the bot's own state.
    ///
    /// The token is sealed with DPAPI under the MACHINE — see <see cref="MachineSecret"/> — because
    /// the manager's login writes it here and a service running as LocalSystem has to read it back.
    /// It is never stored in the clear, and this screen never shows it again once saved: there is
    /// nothing a manager can do with a token they can already see, and plenty somebody else could.
    /// </summary>
    public class TelegramSetupForm : BaseForm
    {
        /// <summary>
        /// Must match the bot service's OutboxMaxAttempts, or this page would disagree with the
        /// worker about which messages have been given up on.
        /// </summary>
        private const int OutboxAttemptCap = 12;

        private readonly BotStore _bot;
        private readonly User _admin;

        private Label _tokenState;
        private Label _codeBox;
        private Label _codeHint;
        private DataGridView _linked;
        private Label _dbPath;

        public TelegramSetupForm(User admin)
        {
            _admin = admin ?? throw new ArgumentNullException(nameof(admin));
            if (!admin.IsAdmin)
                throw new PermissionDeniedException("إعداد تيليجرام متاح للمدير فقط.");

            _bot = new BotStore(new SqliteConnectionFactory(BotStore.DefaultPath));

            Text = "إعداد تيليجرام";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(760, 620);
            StartPosition = FormStartPosition.CenterParent;

            var title = new Label
            {
                Text = "بوت تيليجرام للمدير", Dock = DockStyle.Top, Height = 44,
                Font = Theme.Title(16f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 38, Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0),
                Text = "يتيح للمدير متابعة المخزون والتقارير من الهاتف. للقراءة فقط — لا يمكنه تعديل أي بيانات."
            };

            Controls.Add(BuildLinkedList());
            Controls.Add(BuildCodeSection());
            Controls.Add(BuildTokenSection());
            Controls.Add(intro);
            Controls.Add(title);
            Controls.Add(BuildBottomBar());

            Reload();
        }

        // ---------------- the token ----------------

        private Control BuildTokenSection()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 92 };

            _tokenState = new Label
            {
                Dock = DockStyle.Top, Height = 30, Font = Theme.Base(11.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 56, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 8, 10, 8)
            };
            bar.Controls.Add(Theme.ActionButton("إدخال الرمز السري للبوت", EnterToken, primary: true, width: 210));
            bar.Controls.Add(Theme.ActionButton("حذف الرمز السري", ClearToken, width: 150));

            panel.Controls.Add(bar);
            panel.Controls.Add(_tokenState);
            return panel;
        }

        /// <summary>
        /// Takes the token from BotFather and seals it.
        ///
        /// Entered as a password field so it is not left readable on a screen in a pharmacy, and
        /// never displayed again afterwards.
        /// </summary>
        private void EnterToken()
        {
            string token = Prompt.Show(
                "الرمز السري من BotFather (يبدأ بأرقام ثم :)",
                "إعداد تيليجرام", "", isPassword: true);

            if (string.IsNullOrWhiteSpace(token)) return;
            token = token.Trim();

            // Not validation of the token's authenticity — only Telegram can say that — but enough to
            // catch a paste of the wrong thing entirely, which would otherwise look like a bot that
            // simply refuses to start.
            if (!token.Contains(":") || token.Length < 20)
            {
                Msg.Warn("هذا لا يشبه رمز بوت.\n" +
                         "الرمز من BotFather بالشكل: 8012345678:AAH...\n\n" +
                         "لم يتم حفظ أي شيء.");
                return;
            }

            try
            {
                if (!_bot.SaveToken(token))
                {
                    // Protection failed, so nothing was written. A token stored in the clear would be
                    // worse than no token at all.
                    Msg.Error("تعذّر تشفير الرمز على هذا الجهاز، ولم يُحفظ.");
                    return;
                }

                Msg.Info("تم حفظ الرمز السري مشفّراً على هذا الجهاز.\n\n" +
                         "أعد تشغيل خدمة البوت لتطبيق التغيير:\n" +
                         "sc.exe stop DawaiiTelegramBot\n" +
                         "sc.exe start DawaiiTelegramBot");
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Log.Error("Saving the Telegram token", ex); Msg.Error("تعذّر الحفظ."); }
        }

        private void ClearToken()
        {
            if (!_bot.HasToken()) { Msg.Info("لا يوجد رمز محفوظ."); return; }
            if (!Msg.Confirm("حذف الرمز السري؟ سيتوقف البوت عن العمل حتى إدخال رمز جديد.")) return;

            try { _bot.ClearToken(); Reload(); }
            catch (Exception ex) { Log.Error("Clearing the Telegram token", ex); Msg.Error("تعذّر الحذف."); }
        }

        // ---------------- link codes ----------------

        private Control BuildCodeSection()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 132 };

            _codeBox = new Label
            {
                Dock = DockStyle.Top, Height = 58, Font = Theme.Title(28f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleCenter, Text = "— — — — — —"
            };

            _codeHint = new Label
            {
                Dock = DockStyle.Top, Height = 34, Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "اضغط \"رمز ربط جديد\"، ثم أرسل من تيليجرام: /link والرمز"
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 2, 10, 2)
            };
            bar.Controls.Add(Theme.ActionButton("رمز ربط جديد", NewCode, primary: true, width: 170));

            panel.Controls.Add(bar);
            panel.Controls.Add(_codeHint);
            panel.Controls.Add(_codeBox);
            return panel;
        }

        /// <summary>
        /// Issues a one-time code for the manager who is signed in — not for anybody they might pick.
        ///
        /// That is deliberate: a code links a phone to THIS pharmacy account, so letting an
        /// administrator mint one for a colleague would let them hand out access under someone else's
        /// name, and the audit trail would show the wrong person. Each manager generates their own,
        /// signed in as themselves.
        /// </summary>
        private void NewCode()
        {
            try
            {
                _bot.EnsureSchema();
                string code = _bot.CreateLinkCode(_admin.Id, DateTime.Now);

                // Spaced out: it gets read off a screen and typed into a phone, often by someone
                // holding the phone at arm's length.
                _codeBox.Text = string.Join(" ", code.ToCharArray());
                _codeHint.Text =
                    "أرسل من تيليجرام إلى البوت:  /link " + code + "\n" +
                    "صالح " + (int)BotStore.CodeLifetime.TotalMinutes + " دقائق، ولمرة واحدة فقط.";

                if (!_bot.HasToken())
                    Msg.Warn("لم يتم إدخال الرمز السري للبوت بعد، لذلك لن يستجيب البوت.\n" +
                             "أدخله أولاً من هذه الصفحة.");
            }
            catch (Exception ex)
            {
                Log.Error("Creating a Telegram link code", ex);
                Msg.Error("تعذّر إنشاء رمز الربط.");
            }
        }

        // ---------------- who is linked ----------------

        private Control BuildLinkedList()
        {
            var panel = new Panel { Dock = DockStyle.Fill };

            var heading = new Label
            {
                Text = "الحسابات المرتبطة", Dock = DockStyle.Top, Height = 30,
                Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.TextPrimary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            _linked = new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true,
                MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            Theme.StyleGrid(_linked);
            Col("معرّف تيليجرام", "TelegramUserId", 150);
            Col("حساب دوائي", "ErpUser", 0, fill: true);
            Col("الحالة", "State", 100);
            Col("تاريخ الربط", "LinkedAt", 140);
            Col("آخر استخدام", "LastSeen", 140);

            panel.Controls.Add(_linked);
            panel.Controls.Add(heading);
            return panel;
        }

        private Control BuildBottomBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10)
            };
            bar.Controls.Add(Theme.ActionButton("إلغاء ربط الحساب المحدد", RevokeSelected, width: 200));
            bar.Controls.Add(Theme.ActionButton("تحديث", Reload, width: 110));
            bar.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 110));

            _dbPath = new Label
            {
                Dock = DockStyle.Bottom, Height = 24, Font = Theme.Base(8.5f), ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var holder = new Panel { Dock = DockStyle.Bottom, Height = 82 };
            holder.Controls.Add(bar);
            holder.Controls.Add(_dbPath);
            return holder;
        }

        /// <summary>
        /// Takes a phone's access away. The row is kept rather than deleted, so "who used to have
        /// access" stays answerable — which is the question actually asked after somebody leaves.
        /// </summary>
        private void RevokeSelected()
        {
            int index = _linked.CurrentRow?.Index ?? -1;
            var rows = _bot.All();
            if (index < 0 || index >= rows.Count) { Msg.Info("اختر حساباً."); return; }

            BotUser user = rows[index];
            if (!user.IsActive) { Msg.Info("هذا الحساب غير مرتبط بالفعل."); return; }

            if (!Msg.Confirm("إلغاء ربط الحساب " + user.TelegramUserId + "؟\n" +
                             "سيتوقف البوت عن الاستجابة له فوراً.")) return;

            try { _bot.Revoke(user.TelegramUserId); Reload(); }
            catch (Exception ex) { Log.Error("Revoking a Telegram link", ex); Msg.Error("تعذّر الإلغاء."); }
        }

        // ---------------- refresh ----------------

        private void Reload()
        {
            try
            {
                _bot.EnsureSchema();

                bool hasToken = _bot.HasToken();
                _tokenState.Text = hasToken
                    ? "الرمز السري: محفوظ ومشفّر على هذا الجهاز ✓"
                    : "الرمز السري: غير محفوظ — البوت لن يعمل";
                _tokenState.ForeColor = hasToken ? Theme.Primary : Theme.Danger;

                _linked.DataSource = _bot.All().Select(u => new
                {
                    u.TelegramUserId,
                    ErpUser = NameOf(u.ErpUserId),
                    State = u.IsActive ? "مرتبط" : "ملغى",
                    LinkedAt = Fmt.DateTime(u.LinkedAt),
                    LastSeen = u.LastSeenAt.HasValue ? Fmt.DateTime(u.LastSeenAt.Value) : "—"
                }).ToList();

                // The outbox, surfaced. A queued alert nobody can see is the silent failure this
                // whole pattern otherwise invites: the manager believes they are being warned about
                // low stock while a message has been stuck for a fortnight.
                (int pending, int stuck) = _bot.OutboxHealth(OutboxAttemptCap);
                string queue = stuck > 0
                    ? "   |   رسائل متعطلة: " + stuck + " ⚠"
                    : pending > 0 ? "   |   في الانتظار: " + pending : "";

                _dbPath.Text = "قاعدة بيانات البوت: " + BotStore.DefaultPath + queue;
            }
            catch (Exception ex)
            {
                Log.Error("Loading the Telegram setup page", ex);
                Msg.Error("تعذّر قراءة بيانات البوت.");
            }
        }

        /// <summary>
        /// The pharmacy account behind a link, named. Read from the ERP so the list shows a person
        /// rather than a number — and shows plainly when the account has since been removed.
        /// </summary>
        private static string NameOf(int erpUserId)
        {
            try
            {
                User u = Session.Services?.Users?.GetById(erpUserId);
                if (u == null) return "#" + erpUserId + " (محذوف)";

                string name = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName;
                if (!u.IsActive) return name + " (موقوف)";
                if (!u.IsAdmin) return name + " (ليس مديراً)";
                return name;
            }
            catch { return "#" + erpUserId; }
        }

        private void Col(string header, string prop, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            _linked.Columns.Add(c);
        }
    }
}
