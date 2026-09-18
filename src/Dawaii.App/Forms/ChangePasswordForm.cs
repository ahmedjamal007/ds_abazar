using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;

namespace Dawaii.App.Forms
{
    /// <summary>Lets the current user change their own password.</summary>
    public class ChangePasswordForm : BaseForm
    {
        private TextBox _current, _new, _confirm;

        public ChangePasswordForm()
        {
            Text = "تغيير كلمة المرور";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(380, 260);

            AddLabel("كلمة المرور الحالية", 18);
            _current = AddText(44);
            AddLabel("كلمة المرور الجديدة", 82);
            _new = AddText(108);
            AddLabel("تأكيد كلمة المرور", 146);
            _confirm = AddText(172);

            var ok = new Button { Text = "حفظ", Location = new Point(20, 210), Size = new Size(160, 36) };
            var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Location = new Point(200, 210), Size = new Size(160, 36) };
            Theme.StylePrimaryButton(ok);
            Theme.StyleSecondaryButton(cancel);
            ok.Click += Save;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Save(object sender, EventArgs e)
        {
            if (_new.Text != _confirm.Text)
            {
                Msg.Warn("كلمتا المرور غير متطابقتين.");
                return;
            }
            try
            {
                Session.Services.UserService.ChangeOwnPassword(Session.CurrentUser, _current.Text, _new.Text);
                Msg.Info("تم تغيير كلمة المرور بنجاح.");
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void AddLabel(string text, int y)
            => Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(20, y), Font = Theme.Base(10f), ForeColor = Theme.TextMuted });

        private TextBox AddText(int y)
        {
            var t = new TextBox { Location = new Point(20, y), Size = new Size(340, 28), Font = Theme.Base(12f), UseSystemPasswordChar = true };
            Controls.Add(t);
            return t;
        }
    }
}
