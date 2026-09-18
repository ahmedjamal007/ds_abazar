using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// Prints a POS sale as an invoice on any Windows printer (A4/Letter) via GDI, which shapes
    /// Arabic correctly. Used when no ESC/POS thermal printer is configured, so invoices print on a
    /// normal office printer. <paramref name="showDialog"/> false prints straight to the default
    /// printer (fast at the counter); true lets the user pick a printer (reprint).
    /// </summary>
    public static class InvoicePrinter
    {
        public static void Print(IWin32Window owner, Sale sale, ReceiptInfo info, bool showDialog)
        {
            using (var doc = new PrintDocument())
            {
                doc.DocumentName = "فاتورة " + sale.SaleNumber;
                doc.PrintPage += (s, e) => Render(e, sale, info);

                if (showDialog)
                {
                    using (var dlg = new PrintDialog { Document = doc, UseEXDialog = true })
                        if (dlg.ShowDialog(owner) == DialogResult.OK) doc.Print();
                }
                else
                {
                    doc.Print(); // default printer
                }
            }
        }

        private static void Render(PrintPageEventArgs e, Sale sale, ReceiptInfo info)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Rectangle area = e.MarginBounds;
            // NoWrap + trimming: a value too wide for its column is cut short honestly rather than
            // wrapping into a line that is then clipped, which is how a receipt lost half its total.
            var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap)
                { Trimming = StringTrimming.EllipsisCharacter };
            var rtlCenter = new StringFormat(StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap)
                { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            float y = area.Top;

            using (var titleFont = new Font(Theme.FontFamily, 20, FontStyle.Bold))
            using (var h = new Font(Theme.FontFamily, 11, FontStyle.Bold))
            using (var f = new Font(Theme.FontFamily, 11))
            using (var big = new Font(Theme.FontFamily, 14, FontStyle.Bold))
            {
                // Header
                g.DrawString(info.PharmacyName ?? "دوائي", titleFont, Brushes.Black, new RectangleF(area.Left, y, area.Width, 34), rtlCenter);
                y += 40;
                g.DrawString("فاتورة بيع", h, Brushes.Black, new RectangleF(area.Left, y, area.Width, 22), rtlCenter);
                y += 30;

                g.DrawString($"رقم الفاتورة: {sale.SaleNumber}", f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                y += 22;
                g.DrawString("التاريخ: " + Bidi.Ltr(sale.CreatedAt.ToString("yyyy-MM-dd HH:mm")), f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                y += 22;
                if (!string.IsNullOrEmpty(info.CashierName))
                {
                    g.DrawString($"الكاشير: {info.CashierName}", f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                    y += 22;
                }
                string typeText = sale.SaleType == SaleType.Credit ? "آجل" : "نقدي — " + PaymentLabel(sale.PaymentMethod);
                g.DrawString($"النوع: {typeText}", f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                y += 30;

                // Column headers (RTL order: name | qty | unit price | total)
                float wName = area.Width * 0.45f, wQty = area.Width * 0.20f, wPrice = area.Width * 0.17f, wTotal = area.Width * 0.18f;
                float xName = area.Right - wName;
                float xQty = xName - wQty;
                float xPrice = xQty - wPrice;
                float xTotal = xPrice - wTotal;

                g.DrawString("الصنف", h, Brushes.Black, new RectangleF(xName, y, wName - 4, 20), rtl);
                g.DrawString("الكمية", h, Brushes.Black, new RectangleF(xQty, y, wQty - 4, 20), rtl);
                g.DrawString("السعر", h, Brushes.Black, new RectangleF(xPrice, y, wPrice - 4, 20), rtl);
                g.DrawString("الإجمالي", h, Brushes.Black, new RectangleF(xTotal, y, wTotal - 4, 20), rtl);
                y += 22;
                g.DrawLine(Pens.Black, area.Left, y, area.Right, y);
                y += 6;

                foreach (SaleLine line in sale.Lines)
                {
                    string name = line.ItemName ?? ("#" + line.ItemId);
                    string qty = $"{line.Quantity} {UnitConverter.LabelAr(line.UnitType)}";
                    string price = (line.UnitPrice * line.UnitsEach).ToString("0.##");
                    g.DrawString(name, f, Brushes.Black, new RectangleF(xName, y, wName - 4, 20), rtl);
                    g.DrawString(qty, f, Brushes.Black, new RectangleF(xQty, y, wQty - 4, 20), rtl);
                    g.DrawString(price, f, Brushes.Black, new RectangleF(xPrice, y, wPrice - 4, 20), rtl);
                    g.DrawString(Bidi.Ltr(line.LineTotal.ToString("0.00")), f, Brushes.Black, new RectangleF(xTotal, y, wTotal - 4, 20), rtl);
                    y += 22;
                }

                y += 4;
                g.DrawLine(Pens.Black, area.Left, y, area.Right, y);
                y += 8;

                g.DrawString($"الإجمالي الفرعي: {sale.Subtotal:0.00} {info.Currency}", f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                y += 22;
                if (sale.Discount > 0)
                {
                    g.DrawString($"الخصم: {sale.Discount:0.00} {info.Currency}", f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 20), rtl);
                    y += 22;
                }
                g.DrawString($"الإجمالي: {sale.Total:0.00} {info.Currency}", big, Brushes.Black, new RectangleF(area.Left, y, area.Width, 26), rtl);
                y += 40;

                g.DrawString(info.Footer ?? "شكراً لزيارتكم", f, Brushes.Gray, new RectangleF(area.Left, y, area.Width, 20), rtlCenter);
            }

            e.HasMorePages = false;
        }

        private static string PaymentLabel(string method) => PaymentMethods.LabelAr(method);
    }
}
