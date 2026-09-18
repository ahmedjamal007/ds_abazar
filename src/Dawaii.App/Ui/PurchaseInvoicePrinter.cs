using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using Dawaii.Core.Models;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// Prints a supplier's order on A4 through GDI, which shapes Arabic correctly (V2.2).
    ///
    /// Deliberately not the thermal receipt: this sheet goes in the folder next to the company's own
    /// invoice, so it carries the things that have to be checked against that paper — the company, the
    /// representative who delivered, the invoice number and date, every line with its cost, and where
    /// the payment stands. A long delivery runs onto as many pages as it needs, and every page repeats
    /// the column headings so a page on its own is still readable.
    /// </summary>
    public static class PurchaseInvoicePrinter
    {
        public static void Print(IWin32Window owner, PurchaseInvoice invoice, string pharmacyName, bool showDialog)
        {
            if (invoice == null) return;

            using (var doc = new PrintDocument())
            {
                // Where the renderer has got to between pages. A field on the document would be shared
                // across prints; a local closed over here dies with this one.
                int nextLine = 0;
                int page = 1;

                doc.DocumentName = "فاتورة مورد " + (invoice.InvoiceNumber ?? invoice.Id.ToString());
                doc.PrintPage += (s, e) =>
                {
                    e.HasMorePages = Render(e, invoice, pharmacyName, page, ref nextLine);
                    page++;
                };

                if (showDialog)
                {
                    using (var dlg = new PrintDialog { Document = doc, UseEXDialog = true })
                        if (dlg.ShowDialog(owner) == DialogResult.OK) doc.Print();
                }
                else
                {
                    doc.Print();
                }
            }
        }

        /// <summary>Shows the sheet on screen before it goes near a printer.</summary>
        public static void Preview(IWin32Window owner, PurchaseInvoice invoice, string pharmacyName)
        {
            if (invoice == null) return;

            using (var doc = new PrintDocument())
            {
                int nextLine = 0;
                int page = 1;
                doc.DocumentName = "فاتورة مورد " + (invoice.InvoiceNumber ?? invoice.Id.ToString());
                doc.PrintPage += (s, e) =>
                {
                    e.HasMorePages = Render(e, invoice, pharmacyName, page, ref nextLine);
                    page++;
                };

                using (var preview = new PrintPreviewDialog { Document = doc, WindowState = FormWindowState.Maximized })
                {
                    // The dialog is built in LTR by default; the sheet inside it is RTL either way, but
                    // the toolbar reads correctly for an Arabic user this way.
                    preview.RightToLeft = RightToLeft.Yes;
                    preview.ShowDialog(owner);
                }
            }
        }

        /// <summary>Draws one page. Returns true when lines are still waiting for another.</summary>
        private static bool Render(PrintPageEventArgs e, PurchaseInvoice invoice, string pharmacyName,
            int page, ref int nextLine)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Rectangle area = e.MarginBounds;
            // See InvoicePrinter: nothing on a printed sheet may wrap into a clipped second line.
            var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap)
                { Trimming = StringTrimming.EllipsisCharacter };
            var rtlCenter = new StringFormat(StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap)
                { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            float y = area.Top;

            using (var titleFont = new Font(Theme.FontFamily, 20, FontStyle.Bold))
            using (var h = new Font(Theme.FontFamily, 11, FontStyle.Bold))
            using (var f = new Font(Theme.FontFamily, 11))
            using (var big = new Font(Theme.FontFamily, 14, FontStyle.Bold))
            using (var pen = new Pen(Color.Black, 1))
            {
                if (page == 1)
                {
                    g.DrawString(pharmacyName ?? "دوائي", titleFont, Brushes.Black,
                        new RectangleF(area.Left, y, area.Width, 40), rtlCenter);
                    y += 44;
                    g.DrawString("فاتورة مشتريات من مورد", big, Brushes.Black,
                        new RectangleF(area.Left, y, area.Width, 28), rtlCenter);
                    y += 34;
                    g.DrawLine(pen, area.Left, y, area.Right, y);
                    y += 10;

                    y = Field(g, h, f, area, y, "الشركة:", invoice.SupplierName ?? "—", rtl);
                    y = Field(g, h, f, area, y, "المندوب:", Or(invoice.Representative), rtl);
                    y = Field(g, h, f, area, y, "رقم الفاتورة:", Or(invoice.InvoiceNumber), rtl);
                    y = Field(g, h, f, area, y, "تاريخ الفاتورة:", Bidi.Ltr(Fmt.Date(invoice.InvoiceDate)), rtl);
                    y = Field(g, h, f, area, y, "أدخلها:", Or(invoice.UserName), rtl);
                    y = Field(g, h, f, area, y, "وقت الإدخال:", Bidi.Ltr(Fmt.DateTime(invoice.CreatedAt)), rtl);
                    y += 8;
                }
                else
                {
                    g.DrawString("تابع — فاتورة " + Or(invoice.InvoiceNumber) + "  صفحة " + page, h, Brushes.Black,
                        new RectangleF(area.Left, y, area.Width, 24), rtlCenter);
                    y += 30;
                }

                // Columns run right-to-left across the sheet: the widths are laid out from the right edge.
                float[] w = { 0.34f, 0.10f, 0.12f, 0.16f, 0.14f, 0.14f };
                string[] headers = { "الصنف", "العلب", "أشرطة/علبة", "سعر شراء العلبة", "سعر البيع", "القيمة" };

                y = Row(g, h, area, y, w, headers, rtl, underline: true, pen: pen);

                float bottomReserve = 150;    // room for the totals block on the last page
                var lines = invoice.Lines;
                bool more = false;

                for (; nextLine < lines.Count; nextLine++)
                {
                    if (y > area.Bottom - bottomReserve)
                    {
                        // Only claim another page when lines actually remain; otherwise the totals still
                        // fit and a blank continuation sheet would come out of the printer.
                        more = nextLine < lines.Count;
                        break;
                    }

                    PurchaseInvoiceLine l = lines[nextLine];
                    y = Row(g, f, area, y, w, new[]
                    {
                        l.ItemName ?? "",
                        l.QuantityBoxes.ToString(),
                        l.StripsPerBox.ToString(),
                        Bidi.Ltr(Fmt.Money(l.BoxPurchasePrice)),
                        Bidi.Ltr(Fmt.Money(l.BoxSellingPrice)),
                        Bidi.Ltr(Fmt.Money(l.LineTotal))
                    }, rtl, underline: false, pen: pen);
                }

                if (more) return true;

                y += 6;
                g.DrawLine(pen, area.Left, y, area.Right, y);
                y += 10;

                y = Field(g, h, big, area, y, "إجمالي الفاتورة:", Bidi.Ltr(Fmt.Money(invoice.Total)), rtl);
                y = Field(g, h, f, area, y, "المدفوع:", Bidi.Ltr(Fmt.Money(invoice.AmountPaid)), rtl);
                y = Field(g, h, big, area, y, "المتبقي:", Bidi.Ltr(Fmt.Money(invoice.Outstanding)), rtl);
                y = Field(g, h, f, area, y, "الحالة:", StatusText(invoice.Status), rtl);

                y += 24;
                g.DrawString("توقيع المستلم: ....................................        توقيع المندوب: ....................................",
                    f, Brushes.Black, new RectangleF(area.Left, y, area.Width, 24), rtl);
            }

            return false;
        }

        private static float Field(Graphics g, Font labelFont, Font valueFont, Rectangle area, float y,
            string label, string value, StringFormat rtl)
        {
            g.DrawString(label, labelFont, Brushes.Black, new RectangleF(area.Left, y, area.Width, 24), rtl);
            var labelWidth = g.MeasureString(label, labelFont).Width + 10;
            g.DrawString(value, valueFont, Brushes.Black,
                new RectangleF(area.Left, y, area.Width - labelWidth, 24), rtl);
            return y + 26;
        }

        /// <summary>One row of the table, laid out right-to-left from the sheet's right edge.</summary>
        private static float Row(Graphics g, Font font, Rectangle area, float y, float[] widths,
            string[] cells, StringFormat rtl, bool underline, Pen pen)
        {
            float right = area.Right;
            float height = 22;
            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                float cw = area.Width * widths[i];
                var cell = new RectangleF(right - cw, y, cw, height);
                g.DrawString(cells[i] ?? "", font, Brushes.Black, cell, rtl);
                right -= cw;
            }
            y += height;
            if (underline) { g.DrawLine(pen, area.Left, y, area.Right, y); y += 4; }
            return y;
        }

        private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        private static string StatusText(PurchaseInvoiceStatus status)
        {
            switch (status)
            {
                case PurchaseInvoiceStatus.Paid: return "مدفوعة";
                case PurchaseInvoiceStatus.PartiallyPaid: return "مدفوعة جزئياً";
                default: return "غير مدفوعة";
            }
        }
    }
}
