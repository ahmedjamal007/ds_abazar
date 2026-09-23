using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// One drug's price, typed by hand (V2.3).
    ///
    /// The user types the BOX price, because that is the figure on the shelf label and the figure a
    /// delivery is entered in; the strip and single-unit prices are derived underneath as they type,
    /// so what a customer buying one strip will pay is visible before anything is saved. Nothing here
    /// is rounded for them — a typed price is exactly the price they want — but a "تقريب" button
    /// offers the practical figure if they want it, and a price below cost is pointed out rather than
    /// refused: a deliberate loss-leader is a pharmacist's call to make.
    ///
    /// The result is handed back to the price screen as a pending change, not written here. Every
    /// change, hand-typed or calculated, goes through the same review-and-confirm step.
    /// </summary>
    public class PriceEditForm : BaseForm
    {
        private readonly Item _item;
        private readonly PricingService _pricing;
        private NumericUpDown _box;
        private Label _derived, _warning;

        /// <summary>
        /// The price the user settled on, as a domain plan row — null if they cancelled. A row, not
        /// bare figures, so the caller gets <see cref="PricePlanRow.BelowCost"/> decided by the service
        /// rather than working it out again from the item's packaging.
        /// </summary>
        public PricePlanRow Result { get; private set; }

        public PriceEditForm(Item item, PricePlanFigures suggested = null)
        {
            _item = item ?? throw new ArgumentNullException(nameof(item));
            _pricing = Session.Services.Pricing;

            Text = "تعديل السعر — " + item.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(480, 400);

            var title = new Label
            {
                Text = item.DisplayName,
                Dock = DockStyle.Top, Height = 40, Font = Theme.Title(14f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            decimal costBox = UnitConverter.CostOf(item, UnitType.Box);
            var facts = new Label
            {
                Dock = DockStyle.Top, Height = 78, Font = Theme.Base(11f), ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0),
                Text =
                    "التعبئة: " + item.StripsPerBox + " شريط × " + item.UnitsPerStrip + " حبة\n" +
                    "تكلفة العلبة: " + Fmt.Money(costBox) + "\n" +
                    "السعر الحالي للعلبة: " + (item.SellingPrice.HasValue
                        ? Fmt.Money(UnitConverter.PriceOf(item, UnitType.Box)) : "بدون سعر") +
                    (item.ManualPrice ? "  (يدوي)" : "")
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
                Padding = new Padding(16, 8, 16, 0), RightToLeft = RightToLeft.Yes
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

            _box = new NumericUpDown
            {
                Dock = DockStyle.Fill, Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Increment = 50,
                Font = Theme.Base(13f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center
            };
            AddRow(table, "سعر بيع العلبة الجديد *", _box);

            var round = Theme.ActionButton("تقريب لسعر عملي", RoundIt, width: 180);
            AddRow(table, "", round);

            _derived = new Label
            {
                Dock = DockStyle.Top, Height = 64, Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.Primary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 6, 16, 6)
            };
            _warning = new Label
            {
                Dock = DockStyle.Top, Height = 30, Font = Theme.Base(10.5f), ForeColor = Theme.Danger,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 16, 0)
            };
            _box.ValueChanged += (s, e) => ShowDerived();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton("اعتماد", Accept, primary: true, width: 130));
            bar.Controls.Add(Theme.ActionButton("إلغاء", Close, width: 130));

            Controls.Add(_warning);
            Controls.Add(_derived);
            Controls.Add(table);
            Controls.Add(facts);
            Controls.Add(title);
            Controls.Add(bar);

            // Start from the pending calculated price if there is one, else the current price.
            decimal start = suggested != null ? suggested.BoxPrice
                : item.SellingPrice.HasValue ? UnitConverter.PriceOf(item, UnitType.Box) : costBox;
            _box.Value = Math.Min(_box.Maximum, Math.Max(_box.Minimum, start));
            ShowDerived();
        }

        private void ShowDerived()
        {
            try
            {
                // The service decides the figures AND whether they fall below cost; this screen only
                // shows the answer. It used to repeat the cost arithmetic here, which is how a second
                // interpretation of "box cost" gets into the app.
                PricePlanRow row = _pricing.PreviewManual(_item, _box.Value);
                PricePlanFigures f = row.New;
                _derived.Text =
                    "الشريط: " + Fmt.Money(f.StripPrice) + "     الحبة: " + Fmt.Money(decimal.Round(f.UnitPrice, 2)) +
                    "\nالعلبة: " + Fmt.Money(f.BoxPrice);

                _warning.Text =
                    _item.PurchasePrice <= 0m ? "لا توجد تكلفة شراء مسجلة — لا يمكن التحقق من الربح." :
                    row.BelowCost ? "⚠ السعر أقل من التكلفة (" + Fmt.Money(row.CostPerBox) + ")." :
                    f.BoxPrice == 0m ? "سعر صفر يعني البيع مجاناً." : "";
            }
            catch (DomainException ex) { _warning.Text = ex.Message; }
        }

        /// <summary>Offers the practical figure — by rounding the STRIP, so the box stays an exact multiple.</summary>
        private void RoundIt()
        {
            int strips = Math.Max(1, _item.StripsPerBox);
            decimal strip = BatchPricing.RoundToPractical(
                BatchPricing.StripFromBox(_box.Value, strips), _pricing.RoundingStep);
            _box.Value = Math.Min(_box.Maximum, strip * strips);
        }

        private void Accept()
        {
            _box.Select(0, 0);   // commit a figure still being typed
            try
            {
                PricePlanRow row = _pricing.PreviewManual(_item, _box.Value);
                if (row.BelowCost &&
                    !Msg.Confirm("السعر المدخل أقل من تكلفة الشراء (" + Fmt.Money(row.CostPerBox) + ").\n" +
                                 "هل تريد اعتماده على أي حال؟"))
                    return;

                Result = row;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Log.Error("Manual price", ex); Msg.Error("تعذّر حفظ السعر."); }
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
