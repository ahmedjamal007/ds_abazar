using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// What one invoice actually contained: its lines with the unit, the quantity and the price each
    /// was really sold at, then the invoice's own totals. Both places that used to list invoices by
    /// total alone — the employee's daily sales and a customer's statement — show this underneath the
    /// selected row, so "340.00" can always be read back as the items that made it up.
    ///
    /// The caller passes a sale it loaded with its lines (<c>PosService.GetSale</c>); this control does
    /// no data access of its own, so it is equally usable from a screen that has the sale already.
    /// </summary>
    public class ReceiptDetailView : Panel
    {
        private readonly Label _title;
        private readonly Label _totals;
        private readonly DataGridView _lines;

        public ReceiptDetailView()
        {
            BackColor = Theme.Background;

            _title = new Label
            {
                Dock = DockStyle.Top, Height = 28, Font = Theme.Base(12f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 12, 0)
            };
            _totals = new Label
            {
                Dock = DockStyle.Bottom, Height = 34, Font = Theme.Base(11.5f, FontStyle.Bold),
                ForeColor = Theme.Primary, BackColor = Theme.Surface,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(12, 4, 12, 4)
            };
            _lines = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_lines);
            AddCol("الصنف", 0);
            AddCol("الوحدة", 90);
            AddCol("الكمية", 80);
            AddCol("سعر الوحدة", 130);
            AddCol("القيمة", 140);

            Controls.Add(_lines);
            Controls.Add(_totals);
            Controls.Add(_title);
            ShowMessage("اختر فاتورة لعرض أصنافها.");
        }

        /// <summary>Empties the detail and explains why — nothing selected, or no invoice behind the row.</summary>
        public void ShowMessage(string message)
        {
            _lines.Rows.Clear();
            _title.Text = message;
            _totals.Text = "";
        }

        /// <summary>Fills in one invoice: a row per line, then its subtotal/discount/total.</summary>
        public void ShowSale(Sale sale)
        {
            if (sale == null) { ShowMessage("تعذّر تحميل الفاتورة."); return; }

            _lines.Rows.Clear();
            _title.Text = $"تفاصيل الفاتورة {sale.SaleNumber}  —  {Fmt.DateTime(sale.CreatedAt)}";

            foreach (SaleLine line in sale.Lines)
            {
                int i = _lines.Rows.Add();
                _lines.Rows[i].Cells[0].Value = line.ItemName;
                _lines.Rows[i].Cells[1].Value = UnitConverter.LabelAr(line.UnitType);
                _lines.Rows[i].Cells[2].Value = line.Quantity;
                // The stored price is per single unit; what the customer was charged is per unit SOLD,
                // so a box line shows the box price rather than the price of one tablet inside it.
                _lines.Rows[i].Cells[3].Value = Fmt.Money(line.UnitPrice * line.UnitsEach);
                _lines.Rows[i].Cells[4].Value = Fmt.Money(line.LineTotal);

                // A line the customer partly gave back is dimmed and says how many came back, so the
                // invoice total below still reconciles against what is on the shelf.
                if (line.ReturnedQuantity > 0)
                {
                    _lines.Rows[i].DefaultCellStyle.ForeColor = Theme.TextMuted;
                    _lines.Rows[i].Cells[0].Value += $"  (مرتجع {line.ReturnedQuantity})";
                }
            }

            if (sale.Lines.Count == 0) _title.Text += "  —  لا توجد أصناف مسجلة";

            string totals = $"الإجمالي: {Fmt.Money(sale.Total)}";
            if (sale.Discount > 0)
                totals = $"المجموع: {Fmt.Money(sale.Subtotal)}   |   الخصم: {Fmt.Money(sale.Discount)}   |   " + totals;
            if (sale.RefundedTotal > 0)
                totals += $"   |   المرتجع: {Fmt.Money(sale.RefundedTotal)}   |   الصافي: {Fmt.Money(sale.NetTotal)}";
            _totals.Text = totals;
        }

        private void AddCol(string header, int width)
            => _lines.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Width = width > 0 ? width : 160,
                AutoSizeMode = width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
            });
    }
}
