using System;
using System.Globalization;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Lays out a POS sale as a thermal receipt (V2.2).
    ///
    /// This replaced a GDI layout that printed through the Windows driver, where the app had to guess
    /// the paper width and guessed wrong: it assumed an "80mm" roll means 80mm of ink, while the shop's
    /// head marks 72.1mm, so the last 6mm — every right-aligned Arabic label — was never printed. The
    /// document here is measured in PRINT HEAD DOTS instead (576 across an 80mm head), rasterised, and
    /// sent to the printer as an image. There is no paper size to negotiate and nothing to guess.
    ///
    /// The catalogue is in English and the names are long, so the name column is the widest by some way
    /// and wraps inside its cell rather than being cut — a customer cannot check a receipt against the
    /// box in their hand if the drug is printed as "Am".
    /// </summary>
    public static class SaleReceiptBuilder
    {
        private static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public static ReceiptDocument Build(Sale sale, ReceiptInfo info)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            info = info ?? new ReceiptInfo();

            var doc = new ReceiptDocument();

            // A sale gets its number from the row the store wrote, so it is always 1 or more. Zero
            // means this sale was never saved — the cart printed as a price quote (V2.4) — and such a
            // slip must not be able to pass for proof of purchase. Reading it off the number rather
            // than a flag means ANY unsaved sale that reaches a printer is labelled correctly, however
            // it got there.
            bool quote = sale.SaleNumber <= 0;

            var header = new BoxElement { Padding = 8 };
            header.Children.Add(new TextElement(
                Or(info.PharmacyName, "دوائي"), Align.Center, bold: true, scale: 1.5));
            header.Children.Add(new TextElement(quote ? "عرض سعر" : "إيصال بيع", Align.Center));
            doc.Add(header);
            doc.Add(new SpaceElement { Height = 4 });

            // Number, date and time on one line — the three things a customer or a return desk needs
            // to find this sale again. A quote has no number to give them.
            doc.Add(new RowElement
            {
                Cells = new[]
                {
                    quote ? "غير مباعة" : "فاتورة " + sale.SaleNumber.ToString(En),
                    sale.CreatedAt.ToString("yyyy-MM-dd", En),
                    sale.CreatedAt.ToString("HH:mm", En),
                },
                Weights = new double[] { 1.1, 1, 0.7 },
                Aligns = new[] { Align.Right, Align.Center, Align.Left },
                Bold = true,
            });

            if (quote)
                doc.Add(new TextElement("هذه ليست فاتورة بيع — لم يتم الدفع ولم يُخصم المخزون.", Align.Center));

            doc.Add(new RuleElement());

            if (!string.IsNullOrEmpty(info.CashierName))
                doc.Add(new TextElement("الكاشير: " + info.CashierName, Align.Right));

            string method = sale.SaleType == SaleType.Credit ? "آجل" : PaymentLabel(sale.PaymentMethod);
            doc.Add(new TextElement("الدفع: " + method, Align.Right));

            // The name column carries most of the width because the drugs are named in English.
            var table = new TableElement
            {
                Header = new[] { "الصنف", "السعر", "الكمية", "الإجمالي" },
                Weights = new double[] { 0.46, 0.18, 0.14, 0.22 },
                Aligns = new[] { Align.Right, Align.Center, Align.Center, Align.Center },
            };
            foreach (SaleLine line in sale.Lines)
            {
                table.Rows.Add(new[]
                {
                    line.ItemName ?? ("#" + line.ItemId.ToString(En)),
                    Amount(line.UnitPrice * line.UnitsEach),
                    line.Quantity.ToString(En) + " " + UnitConverter.LabelAr(line.UnitType),
                    Amount(line.LineTotal),
                });
            }
            doc.Add(table);

            if (sale.Discount > 0)
            {
                doc.Add(new RowElement
                {
                    Cells = new[] { "المجموع الفرعي", Amount(sale.Subtotal) },
                    Weights = new double[] { 1, 1 },
                    Aligns = new[] { Align.Right, Align.Left },
                });
                doc.Add(new RowElement
                {
                    Cells = new[] { "الخصم", Amount(sale.Discount) },
                    Weights = new double[] { 1, 1 },
                    Aligns = new[] { Align.Right, Align.Left },
                });
            }

            // The total gets a box of its own: it is the one figure the customer checks.
            var total = new BoxElement { Padding = 8 };
            total.Children.Add(new RowElement
            {
                Cells = new[] { "الإجمالي:", Amount(sale.Total) + " " + Or(info.Currency, "ج.س") },
                Weights = new double[] { 0.42, 0.58 },
                Aligns = new[] { Align.Right, Align.Center },
                Bold = true,
                Scale = 1.3,
            });
            doc.Add(total);

            doc.Add(new SpaceElement { Height = 4 });
            doc.Add(new TextElement(Or(info.Footer, "شكراً لزيارتكم"), Align.Center));
            return doc;
        }

        /// <summary>Two decimals, Latin digits, no thousands separator — what the shop's paper shows.</summary>
        private static string Amount(decimal value) => value.ToString("0.00", En);

        private static string PaymentLabel(string method) => PaymentMethods.LabelAr(method);

        private static string Or(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
