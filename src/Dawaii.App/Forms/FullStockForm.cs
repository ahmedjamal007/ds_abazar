using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// Stock entry (V1.3). Captures the drug's details and its first stock batch in one place: expiry,
    /// quantity in BOXES, strips per box, and the BOX purchase and selling prices. There is no lot
    /// number to type — batches are identified by their expiry and their row number.
    /// Strip prices are shown beside them as read-only values that re-divide as the user types (V1.7) —
    /// nothing here is marked up or multiplied.
    /// The barcode is one field on this screen (V1.9): a drug has a single code, so it is scanned in
    /// with the rest of its details instead of on a second screen that walked unit by unit. Leaving it
    /// empty is fine — the item can be given a printed QR code later from "رموز الصنف".
    /// Opened with an existing item, the item fields are pre-filled and it just adds a new batch to it.
    /// </summary>
    public class FullStockForm : BaseForm
    {
        private readonly Item _existing;                 // null => creating a new item

        private TextBox _name, _generic, _barcode;
        private NumericUpDown _unitsPerStrip, _stripsPerBox;
        private NumericUpDown _boxes, _boxPurchase, _boxSelling, _min, _max;
        private readonly List<NumericUpDown> _numbers = new List<NumericUpDown>();   // every numeric field, for live recalc
        private CheckBox _hasExpiry;
        private DateTimePicker _expiry;
        private Label _stripPurchase, _stripSelling, _totalPreview;

        public FullStockForm(Item existing = null)
        {
            _existing = existing;
            BuildUi();
            if (existing != null) Prefill(existing);
            Recalculate();
        }

        private void BuildUi()
        {
            Text = _existing == null ? "مخزون كامل — صنف جديد" : "مخزون كامل — " + _existing.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            // Never taller than the screen can show: on a 768px laptop the save buttons used to fall under
            // the taskbar. Anything that doesn't fit scrolls inside the form instead.
            int fits = Screen.FromPoint(Cursor.Position).WorkingArea.Height - 56;   // title bar + borders
            ClientSize = new Size(480, Math.Max(420, Math.Min(726, fits)));
            BackColor = Theme.Background;

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(16, 12, 16, 8), RightToLeft = RightToLeft.Yes
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

            Section(table, "بيانات الصنف");
            _name = TextRow(table, "الاسم التجاري *");
            _generic = TextRow(table, "الاسم العلمي");
            _barcode = TextRow(table, "الباركود / رمز QR");
            _unitsPerStrip = Num(table, "حبات في الشريط *", 1, 100000, 1);

            Section(table, "بيانات الدفعة");

            var expPanel = new Panel { Dock = DockStyle.Fill, Height = 30 };
            _hasExpiry = new CheckBox { Text = "لها صلاحية", Dock = DockStyle.Left, Width = 96, Checked = true, Font = Theme.Base(10.5f) };
            _expiry = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Value = DateTime.Today.AddYears(1), Font = Theme.Base(11f) };
            _hasExpiry.CheckedChanged += (s, e) => _expiry.Enabled = _hasExpiry.Checked;
            expPanel.Controls.Add(_expiry);
            expPanel.Controls.Add(_hasExpiry);
            Add(table, "تاريخ الصلاحية", expPanel);

            _boxes = Num(table, "الكمية (علبة) *", 1, 1000000, 1);
            _stripsPerBox = Num(table, "عدد الأشرطة في العلبة *", 1, 100000, 1);

            Section(table, "الأسعار (تُدخل للعلبة فقط)");
            _boxPurchase = Money(table, "سعر شراء العلبة *");
            _boxSelling = Money(table, "سعر بيع العلبة *");
            _stripPurchase = Derived(table, "سعر شراء الشريط (تلقائي)");
            _stripSelling = Derived(table, "سعر بيع الشريط (تلقائي)");

            _totalPreview = new Label { ForeColor = Theme.TextMuted, Font = Theme.Base(10f), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
            Add(table, "إجمالي الدفعة", _totalPreview);

            Section(table, "حدود المخزون");
            _min = Num(table, "الحد الأدنى (حبات)", 0, 1000000, 0);
            _max = Num(table, "الحد الأعلى (حبات)", 0, 1000000, 0);

            // TextChanged as well as ValueChanged: a NumericUpDown only copies its text into Value when it
            // loses focus, so without this the strip prices and the total stay a field behind what is typed.
            foreach (NumericUpDown n in _numbers)
            {
                n.ValueChanged += (s, e) => Recalculate();
                n.TextChanged += (s, e) => Recalculate();
            }

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Background };
            var save = Theme.ActionButton("حفظ", Save, primary: true, width: 205);
            save.Location = new Point(20, 10);
            var cancel = Theme.ActionButton("إلغاء", Close, width: 205);
            cancel.Location = new Point(235, 10);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);

            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 30, Font = Theme.Base(8.5f), ForeColor = Theme.TextMuted,
                Padding = new Padding(18, 0, 18, 0), TextAlign = ContentAlignment.MiddleRight,
                Text = "سعر الشريط = سعر العلبة ÷ عدد الأشرطة — يُحسب تلقائياً ولا يُدخل يدوياً."
            };

            // The fields scroll; the note and the buttons stay pinned to the bottom edge. Added first so
            // docking (which resolves last-added outwards) leaves this panel the remaining space.
            var fields = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            fields.Controls.Add(table);

            Controls.Add(fields);
            Controls.Add(note);
            Controls.Add(buttons);
        }

        private void Prefill(Item i)
        {
            _name.Text = i.NameEn;
            _generic.Text = i.GenericName;
            _barcode.Text = Session.Services.Codes.GetCode(i.Id);
            _unitsPerStrip.Value = Clamp(i.UnitsPerStrip, _unitsPerStrip);
            _stripsPerBox.Value = Clamp(i.StripsPerBox, _stripsPerBox);
            _min.Value = Clamp(i.MinQuantity, _min);
            _max.Value = Clamp(i.MaxQuantity, _max);

            // Seed the box prices from what the item currently sells at, so adding another shipment at
            // an unchanged price is a straight confirm.
            _boxPurchase.Value = Clamp(i.PurchasePrice * i.UnitsPerBox, _boxPurchase);
            if (i.SellingPrice.HasValue)
                _boxSelling.Value = Clamp(i.SellingPrice.Value * i.UnitsPerBox, _boxSelling);
        }

        /// <summary>Re-derives the strip prices and the batch total from the box prices, the strips-per-box
        /// count and the quantity. Bound to every field that feeds those formulas.</summary>
        private void Recalculate()
        {
            int strips = (int)Typed(_stripsPerBox);
            decimal boxPurchase = Typed(_boxPurchase), boxes = Typed(_boxes);
            _stripPurchase.Text = Fmt.Money(BatchPricing.StripFromBox(boxPurchase, strips));
            _stripSelling.Text = Fmt.Money(BatchPricing.StripFromBox(Typed(_boxSelling), strips));
            _totalPreview.Text =
                $"{(int)boxes} علبة = {TotalUnits()} حبة · تكلفة {Fmt.Money(boxes * boxPurchase)}";
        }

        private int TotalUnits()
            => (int)Typed(_boxes) * BatchPricing.UnitsPerBox((int)Typed(_stripsPerBox), (int)Typed(_unitsPerStrip));

        /// <summary>What the field shows right now, clamped to its range — the half-typed text included.
        /// Falls back to the committed value when the box is empty or mid-edit ("-", "1.").</summary>
        private static decimal Typed(NumericUpDown n)
        {
            decimal v;
            if (!decimal.TryParse(n.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out v) &&
                !decimal.TryParse(n.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return n.Value;
            return Clamp(v, n);
        }

        /// <summary>Pushes what is on screen into every field's Value, so saving reads the typed number even
        /// if the field never lost focus (Enter on the last box, a shortcut, a click straight onto a button).</summary>
        private void CommitEdits()
        {
            foreach (NumericUpDown n in _numbers) n.Value = Typed(n);
        }

        // ---------- flow ----------

        private bool ValidateInput()
        {
            CommitEdits();
            if (string.IsNullOrWhiteSpace(_name.Text)) { Msg.Warn("الاسم التجاري مطلوب."); return false; }
            if (_min.Value > 0 && _max.Value > 0 && _max.Value < _min.Value)
            { Msg.Warn("الحد الأعلى يجب أن يكون أكبر من الحد الأدنى."); return false; }
            if (_boxSelling.Value < _boxPurchase.Value &&
                !Msg.Confirm("سعر البيع أقل من سعر الشراء — سيتم البيع بخسارة. متابعة؟"))
                return false;
            return true;
        }

        private void Save()
        {
            if (!ValidateInput()) return;
            try
            {
                User user = Session.CurrentUser;
                int itemId;

                if (_existing == null)
                {
                    var item = new Item
                    {
                        NameEn = _name.Text.Trim(),
                        GenericName = NullIfEmpty(_generic.Text),
                        UnitsPerStrip = (int)_unitsPerStrip.Value,
                        StripsPerBox = (int)_stripsPerBox.Value
                        // Prices stay unset here — ReceiveStock below carries them over from the batch.
                    };
                    itemId = Session.Services.Inventory.CreateItem(user, item);
                }
                else
                {
                    _existing.NameEn = _name.Text.Trim();
                    _existing.GenericName = NullIfEmpty(_generic.Text);
                    _existing.UnitsPerStrip = (int)_unitsPerStrip.Value;
                    _existing.StripsPerBox = (int)_stripsPerBox.Value;
                    Session.Services.Inventory.UpdateItemDetails(user, _existing);
                    itemId = _existing.Id;
                }

                DateTime? expiry = _hasExpiry.Checked ? _expiry.Value.Date : (DateTime?)null;
                Session.Services.Inventory.ReceiveStock(user, itemId, (int)_boxes.Value, expiry,
                    _boxPurchase.Value, _boxSelling.Value, (int)_stripsPerBox.Value, batchNumber: null);
                Session.Services.Inventory.SetStockLevels(user, itemId, (int)_min.Value, (int)_max.Value);

                // The barcode is the last step so a code already owned by another drug cannot cost the
                // user the stock they just typed in — the batch is saved, only the code is refused.
                string code = _barcode.Text.Trim();
                bool coded = false;
                if (code.Length > 0)
                {
                    try { Session.Services.Codes.SetCode(user, itemId, code); coded = true; }
                    catch (DomainException ex) { Msg.Warn("لم يُحفظ الباركود: " + ex.Message); }
                }

                Msg.Info(coded
                    ? "تم حفظ الصنف والمخزون والباركود."
                    : "تم حفظ الصنف والمخزون.");
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        // ---------- builders ----------

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static decimal Clamp(decimal v, NumericUpDown n) => v < n.Minimum ? n.Minimum : (v > n.Maximum ? n.Maximum : v);

        private void Section(TableLayoutPanel table, string title)
        {
            var lbl = new Label { Text = title, Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.Primary, AutoSize = false, Height = 28, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomRight, Margin = new Padding(6, 8, 6, 2) };
            table.Controls.Add(lbl);
            table.SetColumnSpan(lbl, 2);
        }

        private TextBox TextRow(TableLayoutPanel table, string label)
        {
            var box = new TextBox { Dock = DockStyle.Fill, Font = Theme.Base(11f) };
            Add(table, label, box);
            return box;
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

        /// <summary>A calculated field: a bold label, so there is nothing for the user to type into.</summary>
        private Label Derived(TableLayoutPanel table, string label)
        {
            var l = new Label { ForeColor = Theme.Primary, Font = Theme.Base(11.5f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
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
