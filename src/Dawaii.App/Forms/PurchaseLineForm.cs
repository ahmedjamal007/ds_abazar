using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// One medicine on a supplier's invoice: how many boxes came, how they are packed, what they cost
    /// and what they will sell for (V2.1).
    ///
    /// It asks for exactly what a stock batch needs, because that is what the line becomes when the
    /// invoice is saved — the same figures the "إدخال دفعة مخزون" screen asks for, gathered here so a
    /// whole delivery can be typed in one pass instead of one screen per drug.
    /// </summary>
    public class PurchaseLineForm : BaseForm
    {
        private readonly Item _item;

        /// <summary>The line being corrected, or null when one is being added. Kept because a stored
        /// line has an identity — its row and the batch it created — that the corrected copy has to
        /// carry back, or saving would delete the delivery and file it again as a new one.</summary>
        private readonly PurchaseInvoiceLine _editing;

        private NumericUpDown _boxes, _stripsPerBox, _buy, _sell;
        private DateTimePicker _expiry;
        private CheckBox _hasExpiry;
        private TextBox _batchNumber;
        private Label _lineTotal;

        /// <summary>The line the user built, or null if they cancelled.</summary>
        public PurchaseInvoiceLine Result { get; private set; }

        public PurchaseLineForm(Item item, PurchaseInvoiceLine editing = null)
        {
            _item = item ?? throw new ArgumentNullException(nameof(item));
            _editing = editing;

            Text = "صنف في الفاتورة — " + item.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(460, 430);

            var title = new Label
            {
                Text = item.DisplayName,
                Dock = DockStyle.Top, Height = 40, Font = Theme.Title(14f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(16), RightToLeft = RightToLeft.Yes
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

            _boxes = Num(table, "عدد العلب *", 1, 1000000, 1);
            // Packaging is per shipment: the same drug can arrive 10 strips to a box one month and 4 the
            // next, and every derived strip price depends on which it was.
            _stripsPerBox = Num(table, "أشرطة في العلبة *", 1, 1000, Math.Max(1, item.StripsPerBox));
            _buy = Money(table, "سعر شراء العلبة *");
            _sell = Money(table, "سعر بيع العلبة *");
            _batchNumber = TextRow(table, "رقم التشغيلة");

            _hasExpiry = new CheckBox { Text = "للصنف تاريخ صلاحية", AutoSize = true, Checked = true, Font = Theme.Base(11f) };
            AddRow(table, "", _hasExpiry);
            _expiry = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Short, Font = Theme.Base(11f), Value = DateTime.Today.AddYears(2) };
            AddRow(table, "تاريخ الصلاحية", _expiry);
            _hasExpiry.CheckedChanged += (s, e) => _expiry.Enabled = _hasExpiry.Checked;

            _lineTotal = new Label
            {
                Dock = DockStyle.Top, Height = 40, Font = Theme.Base(13f, FontStyle.Bold), ForeColor = Theme.Primary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 6, 16, 6)
            };
            _boxes.ValueChanged += (s, e) => ShowLineTotal();
            _buy.ValueChanged += (s, e) => ShowLineTotal();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton(editing == null ? "إضافة" : "حفظ", Save, primary: true, width: 130));
            bar.Controls.Add(Theme.ActionButton("إلغاء", Close, width: 130));

            Controls.Add(_lineTotal);
            Controls.Add(table);
            Controls.Add(title);
            Controls.Add(bar);

            if (editing != null) Prefill(editing);
            ShowLineTotal();
        }

        private void Prefill(PurchaseInvoiceLine l)
        {
            _boxes.Value = Clamp(l.QuantityBoxes, _boxes);
            _stripsPerBox.Value = Clamp(l.StripsPerBox, _stripsPerBox);
            _buy.Value = Clamp(l.BoxPurchasePrice, _buy);
            _sell.Value = Clamp(l.BoxSellingPrice, _sell);
            _batchNumber.Text = l.BatchNumber ?? "";
            _hasExpiry.Checked = l.ExpiryDate.HasValue;
            if (l.ExpiryDate.HasValue) _expiry.Value = l.ExpiryDate.Value;
        }

        private void ShowLineTotal()
            => _lineTotal.Text = "قيمة السطر: " + Fmt.Money(decimal.Round(_buy.Value * _boxes.Value, 2));

        private void Save()
        {
            // Committing the editors first means a figure still being typed is counted, rather than the
            // value the box held before the caret went in.
            _boxes.Select(0, 0);

            if (_sell.Value < _buy.Value &&
                !Msg.Confirm("سعر البيع أقل من سعر الشراء — سيتم البيع بخسارة. متابعة؟"))
                return;

            Result = new PurchaseInvoiceLine
            {
                // A line already on file keeps its row and its batch: this is the same delivery being
                // corrected, not a second one. Both are zero/null for a line being added.
                Id = _editing != null ? _editing.Id : 0,
                InvoiceId = _editing != null ? _editing.InvoiceId : 0,
                StockBatchId = _editing != null ? _editing.StockBatchId : null,
                ItemId = _item.Id,
                ItemName = _editing != null && !string.IsNullOrWhiteSpace(_editing.ItemName)
                    ? _editing.ItemName          // the name it was delivered under, not today's
                    : _item.DisplayName,
                QuantityBoxes = (int)_boxes.Value,
                StripsPerBox = (int)_stripsPerBox.Value,
                BoxPurchasePrice = decimal.Round(_buy.Value, 2),
                BoxSellingPrice = decimal.Round(_sell.Value, 2),
                BatchNumber = string.IsNullOrWhiteSpace(_batchNumber.Text) ? null : _batchNumber.Text.Trim(),
                ExpiryDate = _hasExpiry.Checked ? _expiry.Value.Date : (DateTime?)null
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---------------- small layout helpers ----------------

        private static decimal Clamp(decimal v, NumericUpDown n) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

        private NumericUpDown Num(TableLayoutPanel t, string label, int min, int max, int value)
        {
            var n = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value,
                Font = Theme.Base(12f), TextAlign = HorizontalAlignment.Center
            };
            AddRow(t, label, n);
            return n;
        }

        private NumericUpDown Money(TableLayoutPanel t, string label)
        {
            var n = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Increment = 1,
                Font = Theme.Base(12f), TextAlign = HorizontalAlignment.Center
            };
            AddRow(t, label, n);
            return n;
        }

        private TextBox TextRow(TableLayoutPanel t, string label)
        {
            var b = new TextBox { Dock = DockStyle.Fill, Font = Theme.Base(12f) };
            AddRow(t, label, b);
            return b;
        }

        private static void AddRow(TableLayoutPanel t, string label, Control editor)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label
            {
                Text = label, Dock = DockStyle.Fill, Font = Theme.Base(11.5f),
                TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(4, 8, 4, 4)
            }, 0, row);
            editor.Margin = new Padding(4, 6, 4, 4);
            t.Controls.Add(editor, 1, row);
        }
    }
}
