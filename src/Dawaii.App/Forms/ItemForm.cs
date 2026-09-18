using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>Create or edit an item's catalog details (FR-INV-01). No prices here: from V1.7 both the
    /// purchase and the selling price are entered per BOX on each stock batch, so this screen carries only
    /// the drug's identity and packaging.
    ///
    /// A drug carries two names (V2.0): the trade name on the box, which is what the receipt prints and
    /// what staff search for, and the scientific name underneath it. Lists and search results write the
    /// pair as "name / scientific name", so the same box is recognisable whichever one the person
    /// asking happens to know.</summary>
    public class ItemForm : BaseForm
    {
        private readonly Item _editing;
        private TextBox _name, _generic, _unitsPerStrip, _stripsPerBox;
        private ComboBox _substitute;
        private List<Item> _substituteChoices = new List<Item>();

        /// <summary>Optional code to pre-fill (used by the unknown-scan flow, FR-QRC-04).</summary>
        public string PrefillCode { get; set; }
        private TextBox _code;

        public Item Result { get; private set; }
        public string EnteredCode => _code.Text.Trim();

        public ItemForm(Item editing = null)
        {
            _editing = editing;
            BuildUi();
            if (_editing != null) LoadItem(_editing);
        }

        private void BuildUi()
        {
            Text = _editing == null ? "صنف جديد" : "تعديل صنف";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(460, 640);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(16), RightToLeft = RightToLeft.Yes
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            _name = Row(table, "الاسم التجاري *");
            _generic = Row(table, "الاسم العلمي");

            _unitsPerStrip = Row(table, "حبات في الشريط *", "1");
            _stripsPerBox = Row(table, "أشرطة في العلبة *", "1");
            _code = Row(table, "رمز باركود/QR");
            // حدود المخزون (الأدنى/الأعلى) تُضبط عند إدخال «مخزون كامل» في شاشة المخزون.

            // V1.2 req 4: mark this drug as a substitute/alternative of another drug.
            _substitute = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(11f) };
            _substitute.Items.Add("— ليس بديلاً —");
            try
            {
                foreach (Item it in Session.Services.Items.Search("", activeOnly: true, limit: 2000))
                {
                    if (_editing != null && it.Id == _editing.Id) continue; // a drug can't substitute itself
                    _substituteChoices.Add(it);
                    _substitute.Items.Add(it.DisplayName);
                }
            }
            catch { /* offline of DB during design-time — leave list empty */ }
            _substitute.SelectedIndex = 0;
            AddRow(table, "بديل لدواء", _substitute);

            var hint = new Label
            {
                Text = "الأسعار تُدخل مع كل دفعة مخزون (سعر العلبة شراءً وبيعاً)، وليس من هذه الشاشة.",
                ForeColor = Theme.TextMuted, Font = Theme.Base(9f), Dock = DockStyle.Top, Height = 30,
                Padding = new Padding(16, 4, 16, 0)
            };

            var ok = new Button { Text = "حفظ", Size = new Size(200, 42), Location = new Point(20, 580) };
            var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Size = new Size(200, 42), Location = new Point(240, 580) };
            Theme.StylePrimaryButton(ok);
            Theme.StyleSecondaryButton(cancel);
            ok.Click += Save;

            Controls.Add(hint);
            Controls.Add(table);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            if (!string.IsNullOrEmpty(PrefillCode)) _code.Text = PrefillCode;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(PrefillCode)) { _code.Text = PrefillCode; _name.Focus(); }
        }

        private void LoadItem(Item i)
        {
            _name.Text = i.NameEn;
            _generic.Text = i.GenericName;
            // Show the code it already carries, so editing an item does not look like it has none —
            // and so re-saving keeps it rather than silently leaving the box empty.
            try { _code.Text = Session.Services.Codes.GetCode(i.Id); } catch { /* no DB — leave empty */ }
            _unitsPerStrip.Text = i.UnitsPerStrip.ToString();
            _stripsPerBox.Text = i.StripsPerBox.ToString();
            if (i.SubstituteOf.HasValue)
            {
                int sIdx = _substituteChoices.FindIndex(x => x.Id == i.SubstituteOf.Value);
                if (sIdx >= 0) _substitute.SelectedIndex = sIdx + 1;
            }
        }

        private void Save(object sender, EventArgs e)
        {
            try
            {
                var item = _editing ?? new Item();
                item.NameEn = _name.Text.Trim();
                item.GenericName = NullIfEmpty(_generic.Text);
                item.UnitsPerStrip = ParseInt(_unitsPerStrip.Text, "حبات في الشريط");
                item.StripsPerBox = ParseInt(_stripsPerBox.Text, "أشرطة في العلبة");
                // Prices are untouched here — the service keeps the stored ones, and they only move
                // when a stock batch is received or edited.
                item.SubstituteOf = _substitute.SelectedIndex <= 0
                    ? (int?)null
                    : _substituteChoices[_substitute.SelectedIndex - 1].Id;
                Result = item;
                // Setting DialogResult on a modal form closes it and makes ShowDialog return OK.
                // (No explicit Close() — that could race the closing and drop the result to Cancel.)
                DialogResult = DialogResult.OK;
            }
            catch (FormatException ex) { Msg.Warn(ex.Message); }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static int ParseInt(string s, string field, bool allowZero = false)
        {
            if (!int.TryParse(s.Trim(), out int v) || v < 0 || (!allowZero && v == 0))
                throw new FormatException($"القيمة في حقل \"{field}\" غير صالحة.");
            return v;
        }

        private TextBox Row(TableLayoutPanel table, string label, string initial = "")
        {
            var box = new TextBox { Dock = DockStyle.Fill, Font = Theme.Base(11f), Text = initial };
            AddRow(table, label, box);
            return box;
        }

        private void AddRow(TableLayoutPanel table, string label, Control input)
        {
            table.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Right, AutoSize = true, Font = Theme.Base(10.5f), Margin = new Padding(6, 8, 6, 8) });
            input.Margin = new Padding(6, 5, 6, 5);
            table.Controls.Add(input);
        }
    }
}
