using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>Collects details for a new employee account — rounded fields per the design system.</summary>
    public class NewUserForm : BaseForm
    {
        private RoundedTextField _username, _fullName, _password;
        private ComboBox _role;

        public string Username => _username.Text.Trim();
        public string FullName => _fullName.Text.Trim();
        public string Password => _password.Text;
        public Role SelectedRole => RoleLabels.At(_role.SelectedIndex);

        public NewUserForm()
        {
            Text = "موظف جديد";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(420, 430);
            BackColor = Theme.Surface;

            int x = 28, w = ClientSize.Width - 56;

            AddLabel("اسم المستخدم", x, 20, w);
            _username = new RoundedTextField(FieldIcon.Person)
            { Location = new Point(x, 42), Size = new Size(w, 48), Placeholder = "مثال: sara" };

            AddLabel("الاسم الكامل", x, 100, w);
            _fullName = new RoundedTextField
            { Location = new Point(x, 122), Size = new Size(w, 48), Placeholder = "اسم الموظف الكامل" };

            AddLabel("كلمة المرور", x, 180, w);
            _password = new RoundedTextField(FieldIcon.Lock, password: true)
            { Location = new Point(x, 202), Size = new Size(w, 48), Placeholder = "4 أحرف على الأقل" };

            AddLabel("الصلاحية", x, 260, w);
            _role = new ComboBox
            {
                Location = new Point(x, 282), Size = new Size(w, 30),
                DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(11.5f),
                FlatStyle = FlatStyle.Flat
            };
            _role.Items.AddRange(RoleLabels.Choices);
            _role.SelectedIndex = 0;

            // Says out loud what the middle option buys, so the manager isn't guessing at the difference.
            var roleHint = new Label
            {
                Text = "«موظف ذو امتيازات» يستطيع أيضاً إدارة الأصناف والمخزون (إضافة/تعديل/حذف واستلام).",
                Font = Theme.Base(9f), ForeColor = Theme.TextMuted, BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleRight,
                Bounds = new Rectangle(x, 316, w, 28)
            };
            Controls.Add(roleHint);

            var ok = new PillButton { Text = "حفظ", DialogResult = DialogResult.OK, Location = new Point(x, 350), Size = new Size((w - 12) / 2, 46) };
            var cancel = new PillButton { Text = "إلغاء", Outline = true, DialogResult = DialogResult.Cancel, Location = new Point(x + (w - 12) / 2 + 12, 350), Size = new Size((w - 12) / 2, 46) };

            Controls.AddRange(new Control[] { _username, _fullName, _password, _role, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void AddLabel(string text, int x, int y, int w)
            => Controls.Add(new Label
            {
                Text = text, Font = Theme.Base(10f), ForeColor = Theme.TextMuted, BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(x, y, w, 18)
            });
    }
}
