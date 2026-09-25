using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// Records a received shipment as a stock batch (FR-INV-02), and edits an existing one.
    ///
    /// The user types only: expiry, quantity in BOXES, strips per box, box purchase price
    /// and box selling price. The two strip prices are read-only mirrors that re-divide on every
    /// keystroke — 3200/box over 4 strips shows 800/strip immediately, and changing the box selling
    /// price from 4000 to 4500 moves the strip from 1000 to 1125 without a save or a refresh.
    /// </summary>
    public class ReceiveStockForm : BaseForm
    {
        private readonly Item _item;
        private readonly StockBatch _editing;          // null => receiving a new batch

        private NumericUpDown _boxes, _stripsPerBox, _boxPurchase, _boxSelling;
        private DateTimePicker _expiry;
        private CheckBox _hasExpiry;
        private Label _stripPurchase, _stripSelling, _totalPreview;
        private readonly List<NumericUpDown> _numbers = new List<NumericUpDown>();   // every numeric field, for live recalc

        /// <summary>The values the user entered — read by the caller after an OK result.
        /// <see cref="BatchNumber"/> is no longer typed anywhere; when editing it echoes back whatever the
        /// batch already carries so saving never wipes a number an older version recorded.</summary>
        public int QuantityBoxes { get; private set; }
        public int StripsPerBox { get; private set; }
        public decimal BoxPurchasePrice { get; private set; }
        public decimal BoxSellingPrice { get; private set; }
        public string BatchNumber { get; private set; }
        public DateTime? Expiry { get; private set; }

        public ReceiveStockForm(Item item, StockBatch editing = null)
        {
            _item = item;
            _editing = editing;
            BuildUi();
            if (_editing != null) LoadBatch(_editing);
            Recalculate();
        }

        private void BuildUi()
        {
            Text = (_editing == null ? "إدخال دفعة مخزون: " : "تعديل دفعة: ") + _item.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(470, 576);
            BackColor = Theme.Background;

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(16, 12, 16, 8), RightToLeft = RightToLeft.Yes
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

            Section(table, "بيانات الدفعة");

            var expPanel = new Panel { Dock = DockStyle.Fill, Height = 30 };
            _hasExpiry = new CheckBox { Text = "لها صلاحية", Dock = DockStyle.Left, Width = 96, Checked = true, Font = Theme.Base(10.5f) };
            _expiry = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Value = DateTime.Today.AddYears(1), Font = Theme.Base(11f) };
            _hasExpiry.CheckedChanged += (s, e) => _expiry.Enabled = _hasExpiry.Checked;
            expPanel.Controls.Add(_expiry);
            expPanel.Controls.Add(_hasExpiry);
            Add(table, "تاريخ الصلاحية", expPanel);

            _boxes = Num(table, "الكمية (علبة) *", 1, 1000000, 1);
            _stripsPerBox = Num(table, "عدد الأشرطة في العلبة *", 1, 100000, Math.Max(1, _item.StripsPerBox));

            Section(table, "الأسعار (تُدخل للعلبة فقط)");
            _boxPurchase = Money(table, "سعر شراء العلبة *");
            _boxSelling = Money(table, "سعر بيع العلبة *");

            // Derived, never editable — the spec's read-only calculated values.
            _stripPurchase = Derived(table, "سعر شراء الشريط (تلقائي)");
            _stripSelling = Derived(table, "سعر بيع الشريط (تلقائي)");

            _totalPreview = new Label
            {
                ForeColor = Theme.TextMuted, Font = Theme.Base(10f), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };
            Add(table, "إجمالي الدفعة", _totalPreview);

            // TextChanged too: NumericUpDown only copies its text into Value on focus loss, so without this
            // the derived prices lag one field behind the typing.
            foreach (NumericUpDown n in _numbers)
            {
                n.ValueChanged += (s, e) => Recalculate();
                n.TextChanged += (s, e) => Recalculate();
            }

            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 40, Font = Theme.Base(8.5f), ForeColor = Theme.TextMuted,
                Padding = new Padding(18, 0, 18, 0), TextAlign = ContentAlignment.MiddleRight,
                Text = "أسعار الشريط تُحسب تلقائياً = سعر العلبة ÷ عدد الأشرطة، ولا يمكن تعديلها يدوياً."
            };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Background };
            var ok = Theme.ActionButton("حفظ", Save, primary: true, width: 200);
            ok.Location = new Point(20, 10);
            var cancel = Theme.ActionButton("إلغاء", Close, width: 200);
            cancel.Location = new Point(235, 10);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(table);
            Controls.Add(note);
            Controls.Add(buttons);
            AcceptButton = ok;
        }

        private void LoadBatch(StockBatch b)
        {
            _hasExpiry.Checked = b.ExpiryDate.HasValue;
            if (b.ExpiryDate.HasValue) _expiry.Value = b.ExpiryDate.Value;
            _stripsPerBox.Value = Clamp(b.StripsPerBox, _stripsPerBox);
            _boxPurchase.Value = Clamp(b.BoxPurchasePrice, _boxPurchase);
            _boxSelling.Value = Clamp(b.BoxSellingPrice, _boxSelling);

            // Quantity is stored in single units; show it back as whole boxes.
            int unitsPerBox = BatchPricing.UnitsPerBox(b.StripsPerBox, b.UnitsPerStrip);
            _boxes.Value = Clamp(Math.Max(1, b.QuantityUnits / Math.Max(1, unitsPerBox)), _boxes);
        }

        /// <summary>Re-derives both strip prices (and the batch total) from whatever is in the box-price
        /// and strips-per-box fields right now. Wired to every input that feeds the formula.</summary>
        private void Recalculate()
        {
            int strips = (int)Typed(_stripsPerBox);
            decimal boxPurchase = Typed(_boxPurchase);
            _stripPurchase.Text = Fmt.Money(BatchPricing.StripFromBox(boxPurchase, strips));
            _stripSelling.Text = Fmt.Money(BatchPricing.StripFromBox(Typed(_boxSelling), strips));

            int boxes = (int)Typed(_boxes);
            int units = boxes * BatchPricing.UnitsPerBox(strips, Math.Max(1, _item.UnitsPerStrip));
            _totalPreview.Text =
                $"{boxes} علبة = {units} حبة · تكلفة {Fmt.Money(boxes * boxPurchase)}";
        }

        /// <summary>What the field shows right now, clamped to its range — half-typed text included.
        /// Falls back to the committed value when the box is empty or mid-edit.</summary>
        private static decimal Typed(NumericUpDown n)
        {
            // One rule for reading a typed amount, in Dawaii.Core, tested under every culture. The two
            // parses that used to be here read "1250.50" as 125050 on a dot-grouping machine.
            decimal v;
            return MoneyInput.TryParse(n.Text, out v) ? Clamp(v, n) : n.Value;
        }

        /// <summary>Pushes what is on screen into every field's Value before the form is read for saving.</summary>
        private void CommitEdits()
        {
            foreach (NumericUpDown n in _numbers) n.Value = Typed(n);
        }

        private void Save()
        {
            CommitEdits();
            if (_boxSelling.Value < _boxPurchase.Value &&
                !Msg.Confirm("سعر البيع أقل من سعر الشراء — سيتم البيع بخسارة. متابعة؟"))
                return;

            QuantityBoxes = (int)_boxes.Value;
            StripsPerBox = (int)_stripsPerBox.Value;
            BoxPurchasePrice = _boxPurchase.Value;
            BoxSellingPrice = _boxSelling.Value;
            BatchNumber = _editing?.BatchNumber;
            Expiry = _hasExpiry.Checked ? _expiry.Value.Date : (DateTime?)null;
            DialogResult = DialogResult.OK;
        }

        // ---------- builders ----------

        private static decimal Clamp(decimal v, NumericUpDown n) => v < n.Minimum ? n.Minimum : (v > n.Maximum ? n.Maximum : v);

        private void Section(TableLayoutPanel table, string title)
        {
            var lbl = new Label
            {
                Text = title, Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.Primary,
                AutoSize = false, Height = 28, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomRight, Margin = new Padding(6, 8, 6, 2)
            };
            table.Controls.Add(lbl);
            table.SetColumnSpan(lbl, 2);
        }

        private NumericUpDown Num(TableLayoutPanel table, string label, int min, int max, int val)
        {
            var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = val, Font = Theme.Base(12f) };
            Add(table, label, n);
            _numbers.Add(n);
            return n;
        }

        private NumericUpDown Money(TableLayoutPanel table, string label)
        {
            var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Increment = 50m, Font = Theme.Base(12f) };
            Add(table, label, n);
            _numbers.Add(n);
            return n;
        }

        /// <summary>A calculated field: shown as a bold label, so there is no control to type into.</summary>
        private Label Derived(TableLayoutPanel table, string label)
        {
            var l = new Label
            {
                ForeColor = Theme.Primary, Font = Theme.Base(11.5f, FontStyle.Bold),
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight
            };
            Add(table, label, l);
            return l;
        }

        private void Add(TableLayoutPanel table, string label, Control input)
        {
            table.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Right, AutoSize = true, Font = Theme.Base(10.5f), Margin = new Padding(6, 9, 6, 6) });
            input.Margin = new Padding(6, 5, 6, 5);
            table.Controls.Add(input);
        }
    }
}
