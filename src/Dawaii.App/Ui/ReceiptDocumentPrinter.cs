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
    /// Prints a POS sale as a narrow thermal-style receipt (80mm roll) on any Windows printer via GDI,
    /// which shapes Arabic correctly. Used for the cashier at the counter (admins print the wider A4
    /// invoice via <see cref="InvoicePrinter"/>).
    ///
    /// Two rules govern everything here, both learned from a receipt that came off a real roll wrong:
    ///
    /// 1. NOTHING WRAPS. Every line is drawn with <see cref="StringFormatFlags.NoWrap"/> into a rect one
    ///    line high. Without it, a line wider than the paper wraps, the overflow falls outside the rect
    ///    and is clipped, and what reaches the customer is a receipt with "الإجمالي" printed as "لي" and
    ///    a column of stray letters down the edge. A driver that ignores the custom paper size, a long
    ///    pharmacy name, a big total — any of them did it. Trimming to an ellipsis degrades honestly
    ///    instead.
    ///
    /// 2. LATIN RUNS ARE ISOLATED. A date or an amount dropped into an Arabic line is reordered by the
    ///    bidirectional algorithm: "2026-08-19 22:49" came out as "22:49 19-08-2026". Each such run is
    ///    wrapped in LEFT-TO-RIGHT MARKs so it is laid out as one unit inside the Arabic sentence.
    ///
    /// Labels and their values are also drawn as two separate calls — label to the right, value to the
    /// left — so a long number can never push its own label off the paper.
    /// </summary>
    public static class ReceiptDocumentPrinter
    {
        /// <summary>
        /// Fallback only. An "80mm" roll is not 80mm of ink: the XP-80C in the shop prints a 72.1mm
        /// strip and reports 283.74 hundredths, while this used to hard-code 315. The layout ran 6mm
        /// past what the head can mark, and because every line is right-aligned it was the RIGHT edge
        /// that fell off — taking every label with it. The customer got a receipt reading "لي:" where
        /// "الإجمالي:" should be, and a drug name cut to "Am". The width now comes from the driver.
        /// </summary>
        private const int FallbackWidthHundredths = 283;


        public static void Print(IWin32Window owner, Sale sale, ReceiptInfo info, bool showDialog)
        {
            using (PrintDocument doc = BuildDocument(sale, info))
            {
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

        /// <summary>Shows the receipt on screen before any paper is used — the only way to check a roll
        /// layout without printing one to look at it.</summary>
        public static void Preview(IWin32Window owner, Sale sale, ReceiptInfo info)
        {
            using (PrintDocument doc = BuildDocument(sale, info))
            using (var preview = new PrintPreviewDialog { Document = doc, WindowState = FormWindowState.Maximized })
            {
                preview.RightToLeft = RightToLeft.Yes;
                preview.ShowDialog(owner);
            }
        }

        private static PrintDocument BuildDocument(Sale sale, ReceiptInfo info)
        {
            var doc = new PrintDocument();
            doc.DocumentName = "إيصال " + sale.SaleNumber;
            int heightHundredths = 200 + (sale.Lines.Count + 8) * 22;
            doc.DefaultPageSettings.PaperSize =
                new PaperSize("Receipt", RollWidth(doc.PrinterSettings), heightHundredths);
            doc.DefaultPageSettings.Margins = new Margins(8, 8, 8, 8);
            doc.PrintPage += (s, e) => Render(e, sale, info);
            return doc;
        }

        /// <summary>
        /// How wide this printer can actually mark, in hundredths of an inch.
        ///
        /// Asked of the driver rather than assumed, and taken as the SMALLER of the paper it is loaded
        /// with and the strip it can print on that paper — the two differ, and it is the smaller that
        /// decides what reaches the customer. Overriding the paper size with a guess makes the driver
        /// report the guess back, which is how the mismatch stayed invisible.
        /// </summary>
        private static int RollWidth(PrinterSettings printer)
        {
            try
            {
                PageSettings def = printer.DefaultPageSettings;
                int width = def.PaperSize != null && def.PaperSize.Width > 100
                    ? def.PaperSize.Width
                    : FallbackWidthHundredths;

                float printable = def.PrintableArea.Width;
                if (printable > 100 && printable < width) width = (int)Math.Floor(printable);

                // A driver that answers with something absurd is not trusted over a known-good roll.
                return width >= 150 && width <= 400 ? width : FallbackWidthHundredths;
            }
            catch
            {
                return FallbackWidthHundredths;   // no printer installed, or a driver that refuses to say
            }
        }

        private static void Render(PrintPageEventArgs e, Sale sale, ReceiptInfo info)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Last line of defence: never lay out past what the head can mark, whatever the page
            // settings claim. Anything drawn beyond this is simply not on the paper the customer holds.
            Rectangle area = e.MarginBounds;
            float printableRight = e.PageSettings.PrintableArea.Width;
            if (printableRight > 50 && area.Right > printableRight)
                area = Rectangle.FromLTRB(area.Left, area.Top, (int)Math.Floor(printableRight) - 2, area.Bottom);
            if (area.Width < 40)
                area = new Rectangle(area.Left, area.Top, Math.Max(40, e.PageBounds.Width - 16), area.Height);

            using (StringFormat center = Fmt(StringAlignment.Center))
            using (StringFormat right = Fmt(StringAlignment.Near))   // RTL "Near" is the right edge
            using (StringFormat left = Fmt(StringAlignment.Far))
            using (var title = new Font(Theme.FontFamily, 13, FontStyle.Bold))
            using (var h = new Font(Theme.FontFamily, 8, FontStyle.Bold))
            using (var f = new Font(Theme.FontFamily, 8))
            using (var big = new Font(Theme.FontFamily, 11, FontStyle.Bold))
            {
                float y = area.Top;

                // The pharmacy's name is whatever the owner typed; a long one shrinks rather than
                // wrapping into the line below it.
                using (Font fitted = Fit(g, info.PharmacyName ?? "دوائي", title, area.Width))
                    y = Line(g, fitted, Brushes.Black, area, y, info.PharmacyName ?? "دوائي", center);

                y = Line(g, h, Brushes.Black, area, y, "إيصال بيع", center);
                y += 4;

                y = Pair(g, f, area, y, "رقم:", Num(sale.SaleNumber.ToString()), right, left);
                y = Pair(g, f, area, y, "التاريخ:", Num(sale.CreatedAt.ToString("yyyy-MM-dd HH:mm")), right, left);
                if (!string.IsNullOrEmpty(info.CashierName))
                    y = Pair(g, f, area, y, "الكاشير:", info.CashierName, right, left);

                string method = sale.SaleType == SaleType.Credit ? "آجل" : PaymentLabel(sale.PaymentMethod);
                y = Pair(g, f, area, y, "الدفع:", method, right, left);
                y += 3;

                g.DrawLine(Pens.Black, area.Left, y, area.Right, y);
                y += 4;

                // The drug's name gets a line to itself, across the whole roll. The catalogue is in
                // English and the names are long — "Amoxicillin capsules BP 250mg / amoxicillin" does
                // not fit beside a price on 72mm, and a customer cannot check a receipt that says "Am".
                // It is the one place wrapping is wanted, capped at two lines so a freak name cannot
                // push the total off the bottom.
                using (StringFormat wrap = new StringFormat(StringFormatFlags.DirectionRightToLeft)
                       { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter })
                foreach (SaleLine line in sale.Lines)
                {
                    string name = line.ItemName ?? ("#" + line.ItemId);
                    int lh = LineHeight(g, f);
                    float needed = g.MeasureString(name, f, area.Width, wrap).Height;
                    int nameHeight = Math.Max(lh, Math.Min((int)Math.Ceiling(needed), lh * 2));

                    g.DrawString(name, f, Brushes.Black, new RectangleF(area.Left, y, area.Width, nameHeight), wrap);
                    y += nameHeight;

                    // Underneath it, what was taken and what it came to — quantity right, money left.
                    string qty = line.Quantity + " " + UnitConverter.LabelAr(line.UnitType) + " × " +
                                 Num((line.UnitPrice * line.UnitsEach).ToString("0.##"));
                    g.DrawString(qty, f, Brushes.Gray, new RectangleF(area.Left, y, area.Width, lh), right);
                    g.DrawString(Num(line.LineTotal.ToString("0.00")), f, Brushes.Black,
                        new RectangleF(area.Left, y, area.Width, lh), left);
                    y += lh + 3;
                }

                g.DrawLine(Pens.Black, area.Left, y, area.Right, y);
                y += 5;

                if (sale.Discount > 0)
                    y = Pair(g, f, area, y, "الخصم:", Num(sale.Discount.ToString("0.00")), right, left);

                // The total is the one line that must never be trimmed, so it gets the whole width with
                // the label on one side and the figure on the other.
                y = Pair(g, big, area, y, "الإجمالي:", Num(sale.Total.ToString("0.00") + " " + info.Currency), right, left);
                y += 6;

                Line(g, f, Brushes.Gray, area, y, info.Footer ?? "شكراً لزيارتكم", center);
            }

            e.HasMorePages = false;
        }

        /// <summary>A single-line, never-wrapping, ellipsis-trimmed format.</summary>
        private static StringFormat Fmt(StringAlignment alignment)
            => new StringFormat(StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap)
            {
                Alignment = alignment,
                Trimming = StringTrimming.EllipsisCharacter
            };

        /// <summary>Isolates a Latin/numeric run — see <see cref="Bidi"/>.</summary>
        private static string Num(string value) => Bidi.Ltr(value);

        private static int LineHeight(Graphics g, Font font) => (int)Math.Ceiling(font.GetHeight(g)) + 2;

        /// <summary>Draws one whole-width line and returns the next y.</summary>
        private static float Line(Graphics g, Font font, Brush brush, Rectangle area, float y, string text, StringFormat format)
        {
            int lh = LineHeight(g, font);
            g.DrawString(text ?? "", font, brush, new RectangleF(area.Left, y, area.Width, lh), format);
            return y + lh;
        }

        /// <summary>Label to the right, value to the left, drawn separately so neither can displace the
        /// other however long the value gets.</summary>
        private static float Pair(Graphics g, Font font, Rectangle area, float y, string label, string value,
            StringFormat right, StringFormat left)
        {
            int lh = LineHeight(g, font);
            float labelWidth = g.MeasureString(label, font).Width + 6;
            var valueArea = new RectangleF(area.Left, y, Math.Max(20, area.Width - labelWidth), lh);

            g.DrawString(label, font, Brushes.Black, new RectangleF(area.Left, y, area.Width, lh), right);
            g.DrawString(value ?? "", font, Brushes.Black, valueArea, left);
            return y + lh;
        }

        /// <summary>Shrinks a font until the text fits the width, so a long pharmacy name never wraps.</summary>
        private static Font Fit(Graphics g, string text, Font preferred, int width)
        {
            float size = preferred.Size;
            while (size > 6f)
            {
                // Measured with a throwaway font disposed each round: this renders per receipt, and
                // leaked GDI handles are how a till stops printing after a busy day.
                using (var probe = new Font(preferred.FontFamily, size, preferred.Style))
                    if (g.MeasureString(text, probe).Width <= width) break;
                size -= 0.5f;
            }
            return new Font(preferred.FontFamily, size, preferred.Style);
        }

        private static string PaymentLabel(string method) => PaymentMethods.LabelAr(method);
    }
}
