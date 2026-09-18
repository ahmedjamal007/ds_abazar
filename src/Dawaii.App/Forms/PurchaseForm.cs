using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;

namespace Dawaii.App.Forms
{
    /// <summary>Records a purchase (V1.4 "المشتريات"): goods bought from someone who came to sell —
    /// bags (أكياس) and other supplies. Standalone spend log, not tied to the medicine catalog.</summary>
    public class PurchaseForm : BaseForm
    {
        private TextBox _supplier, _description, _note;
        private NumericUpDown _quantity, _unitPrice;
        private Label _total;

        public PurchaseForm()
        {
            Text = "تسجيل مشترى";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(430, 420);

            AddLabel("البيان (ماذا اشتُري؟)", 16);
            _description = new TextBox { Location = new Point(20, 38), Size = new Size(390, 28), Font = Theme.Base(12f) };

            AddLabel("المورد / البائع (اختياري)", 76);
            _supplier = new TextBox { Location = new Point(20, 98), Size = new Size(390, 28), Font = Theme.Base(12f) };

            AddLabel("الكمية", 136, x: 220, width: 190);
            _quantity = new NumericUpDown { Location = new Point(220, 158), Size = new Size(190, 28), Minimum = 1, Maximum = 1000000, Value = 1, Font = Theme.Base(12f) };
            AddLabel("سعر الوحدة", 136, x: 20, width: 190);
            _unitPrice = new NumericUpDown { Location = new Point(20, 158), Size = new Size(190, 28), Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Font = Theme.Base(12f) };
            _quantity.ValueChanged += (s, e) => UpdateTotal();
            _unitPrice.ValueChanged += (s, e) => UpdateTotal();

            _total = new Label { Location = new Point(20, 198), Size = new Size(390, 26), Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.Primary, TextAlign = ContentAlignment.MiddleRight };

            AddLabel("ملاحظة (اختياري)", 234);
            _note = new TextBox { Location = new Point(20, 256), Size = new Size(390, 28), Font = Theme.Base(11f) };

            var ok = Theme.ActionButton("حفظ", Save, primary: true, width: 190);
            ok.Location = new Point(20, 316);
            var cancel = Theme.ActionButton("إلغاء", Close, width: 190);
            cancel.Location = new Point(220, 316);

            Controls.AddRange(new Control[] { _supplier, _description, _quantity, _unitPrice, _total, _note, ok, cancel });
            UpdateTotal();
        }

        private void UpdateTotal()
            => _total.Text = "الإجمالي: " + Fmt.Money(_unitPrice.Value * _quantity.Value);

        private void Save()
        {
            try
            {
                Session.Services.Purchases.AddPurchase(Session.CurrentUser,
                    _description.Text.Trim(), (int)_quantity.Value, _unitPrice.Value,
                    _supplier.Text.Trim(), _note.Text.Trim());
                Msg.Info("تم تسجيل المشترى.");
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void AddLabel(string text, int y, int x = 20, int width = 390)
            => Controls.Add(new Label { Text = text, AutoSize = false, Location = new Point(x, y), Size = new Size(width, 20), Font = Theme.Base(10f), ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleRight });
    }
}
