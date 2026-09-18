using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>A tiny single-field input dialog (RTL).</summary>
    public static class Prompt
    {
        public static string Show(string label, string caption, string initial = "", bool isPassword = false)
        {
            using (var form = new BaseForm())
            {
                form.Text = caption;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ClientSize = new Size(360, 150);

                var lbl = new Label { Text = label, AutoSize = true, Location = new Point(20, 18), Font = Theme.Base(11f) };
                var box = new TextBox
                {
                    Location = new Point(20, 46), Size = new Size(320, 28), Text = initial,
                    Font = Theme.Base(12f), UseSystemPasswordChar = isPassword
                };
                var ok = new Button { Text = "موافق", DialogResult = DialogResult.OK, Location = new Point(20, 95), Size = new Size(150, 36) };
                var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Location = new Point(190, 95), Size = new Size(150, 36) };
                Theme.StylePrimaryButton(ok);
                Theme.StyleSecondaryButton(cancel);

                form.Controls.Add(lbl);
                form.Controls.Add(box);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                form.ActiveControl = box;

                return form.ShowDialog() == DialogResult.OK ? box.Text : null;
            }
        }

        /// <summary>
        /// Two fields in one dialog — a name and the thing that goes with it (login name, phone).
        /// Asking twice in a row reads as two unrelated questions and gives no way back to the first
        /// answer after the second box has opened.
        /// Returns the two values in order, or null when the user cancels.
        /// </summary>
        public static string[] ShowTwo(string label1, string label2, string caption,
            string initial1 = "", string initial2 = "")
        {
            using (var form = new BaseForm())
            {
                form.Text = caption;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ClientSize = new Size(360, 218);

                var lbl1 = new Label { Text = label1, AutoSize = true, Location = new Point(20, 18), Font = Theme.Base(11f) };
                var box1 = new TextBox { Location = new Point(20, 46), Size = new Size(320, 28), Text = initial1, Font = Theme.Base(12f) };
                var lbl2 = new Label { Text = label2, AutoSize = true, Location = new Point(20, 86), Font = Theme.Base(11f) };
                var box2 = new TextBox { Location = new Point(20, 114), Size = new Size(320, 28), Text = initial2, Font = Theme.Base(12f) };

                var ok = new Button { Text = "موافق", DialogResult = DialogResult.OK, Location = new Point(20, 163), Size = new Size(150, 36) };
                var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Location = new Point(190, 163), Size = new Size(150, 36) };
                Theme.StylePrimaryButton(ok);
                Theme.StyleSecondaryButton(cancel);

                form.Controls.Add(lbl1);
                form.Controls.Add(box1);
                form.Controls.Add(lbl2);
                form.Controls.Add(box2);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                form.ActiveControl = box1;

                return form.ShowDialog() == DialogResult.OK
                    ? new[] { box1.Text, box2.Text }
                    : null;
            }
        }

        /// <summary>The same dialog with a drop-down instead of a text box. Returns the chosen index,
        /// or -1 when the user cancels.</summary>
        public static int Choose(string label, string caption, object[] choices, int selected = 0)
        {
            using (var form = new BaseForm())
            {
                form.Text = caption;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ClientSize = new Size(360, 150);

                var lbl = new Label { Text = label, AutoSize = true, Location = new Point(20, 18), Font = Theme.Base(11f) };
                var combo = new ComboBox
                {
                    Location = new Point(20, 46), Size = new Size(320, 28),
                    DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(12f)
                };
                combo.Items.AddRange(choices);
                if (selected >= 0 && selected < combo.Items.Count) combo.SelectedIndex = selected;

                var ok = new Button { Text = "موافق", DialogResult = DialogResult.OK, Location = new Point(20, 95), Size = new Size(150, 36) };
                var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Location = new Point(190, 95), Size = new Size(150, 36) };
                Theme.StylePrimaryButton(ok);
                Theme.StyleSecondaryButton(cancel);

                form.Controls.Add(lbl);
                form.Controls.Add(combo);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                form.ActiveControl = combo;

                return form.ShowDialog() == DialogResult.OK ? combo.SelectedIndex : -1;
            }
        }
    }
}
