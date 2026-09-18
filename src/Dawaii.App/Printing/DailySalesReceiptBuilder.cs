using System;
using System.Globalization;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Lays out one employee's day as a thermal slip — "تقرير مبيعات يومي" (V2.3).
    ///
    /// It goes out the same way a counter receipt does, on the same head, because that is the printer
    /// the employee is standing next to at the end of a shift. The slip answers the questions a manager
    /// asks when the drawer is counted: who was on, when they clocked in, how many invoices, how much,
    /// and how much of it by each way of paying — the last as a table, so كاش and بنكك and فوري and
    /// أوكاش can be checked against the drawer, the bank app and the wallet app one line at a time.
    ///
    /// The per-method figures are NET of returns and sum to the net total; the gross total and the
    /// returns are shown above them so the slip reconciles with itself top to bottom.
    /// </summary>
    public static class DailySalesReceiptBuilder
    {
        private static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public static ReceiptDocument Build(EmployeeDaySheet sheet, User employee, DateTime from, DateTime toExclusive, ReceiptInfo info)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));
            if (employee == null) throw new ArgumentNullException(nameof(employee));
            info = info ?? new ReceiptInfo();

            var doc = new ReceiptDocument();

            var header = new BoxElement { Padding = 8 };
            header.Children.Add(new TextElement(Or(info.PharmacyName, "دوائي"), Align.Center, bold: true, scale: 1.4));
            header.Children.Add(new TextElement("تقرير مبيعات يومي", Align.Center, bold: true, scale: 1.1));
            doc.Add(header);
            doc.Add(new SpaceElement { Height = 4 });

            // One calendar day is the everyday case; a range (the manager's drill-down) shows both ends.
            DateTime lastDay = toExclusive.AddDays(-1).Date;
            // Dates and times are wrapped as left-to-right islands, or the bidi algorithm prints the
            // time before the date — the same reversal the receipt's own date line had.
            string period = Dawaii.App.Ui.Bidi.Ltr(lastDay <= from.Date
                ? from.ToString("yyyy-MM-dd", En)
                : from.ToString("yyyy-MM-dd", En) + " → " + lastDay.ToString("yyyy-MM-dd", En));

            doc.Add(Pair("الوردية:", employee.FullName ?? employee.Username));
            doc.Add(Pair("التاريخ:", period));
            doc.Add(Pair("الحضور:", sheet.FirstLoginAt.HasValue
                ? sheet.FirstLoginAt.Value.ToString("HH:mm", En)
                : "لم يُسجَّل"));
            doc.Add(Pair("طُبع في:", Dawaii.App.Ui.Bidi.Ltr(DateTime.Now.ToString("yyyy-MM-dd HH:mm", En))));
            doc.Add(new RuleElement());

            doc.Add(Pair("عدد الفواتير:", sheet.SalesCount.ToString(En), bold: true));
            doc.Add(Pair("إجمالي المبيعات:", Amount(sheet.SalesTotal, info), bold: true, scale: 1.15));
            if (sheet.ReturnedTotal > 0m)
            {
                doc.Add(Pair("المرتجعات:", Amount(sheet.ReturnedTotal, info)));
                doc.Add(Pair("الصافي:", Amount(sheet.NetTotal, info), bold: true));
            }
            doc.Add(new SpaceElement { Height = 4 });

            // The table: one line per way of paying, every method present even at zero.
            var table = new TableElement
            {
                Header = new[] { "طريقة الدفع", "عدد الفواتير", "الإجمالي" },
                Weights = new double[] { 0.36, 0.26, 0.38 },
                Aligns = new[] { Align.Right, Align.Center, Align.Center },
            };
            foreach (PaymentBreakdownRow row in sheet.ByPaymentMethod)
                table.Rows.Add(new[] { row.LabelAr, row.Count.ToString(En), Amount(row.Total, null) });
            doc.Add(table);

            // The net total under the table is the figure the table's rows add up to.
            var total = new BoxElement { Padding = 8 };
            total.Children.Add(new RowElement
            {
                Cells = new[] { "الإجمالي:", Amount(sheet.NetTotal, info) },
                Weights = new double[] { 0.42, 0.58 },
                Aligns = new[] { Align.Left, Align.Center },     // see Pair(): Left = physical right
                Bold = true,
                Scale = 1.25,
            });
            doc.Add(total);

            if (sheet.ExpensesCount > 0)
            {
                doc.Add(new SpaceElement { Height = 4 });
                doc.Add(Pair("مصروفات الموظف:", Amount(sheet.ExpensesTotal, info) + "  (" + sheet.ExpensesCount.ToString(En) + ")"));
            }

            doc.Add(new SpaceElement { Height = 6 });
            doc.Add(new TextElement("توقيع الموظف: ______________", Align.Left));   // physical right
            doc.Add(new TextElement("توقيع المدير: ______________", Align.Left));
            return doc;
        }

        /// <summary>
        /// A label on the right, its value on the left — the shape every line of the slip shares.
        ///
        /// Note the aligns: the renderer lays text out right-to-left, and under that flow
        /// <see cref="Align.Left"/> is the START of the line — the physical RIGHT edge — and
        /// <see cref="Align.Right"/> the end, the physical left. The names are logical, not physical
        /// (verified by rendering; the sale receipt is built on the same convention).
        /// </summary>
        private static RowElement Pair(string label, string value, bool bold = false, double scale = 1.0)
            => new RowElement
            {
                Cells = new[] { label, value },
                Weights = new double[] { 0.45, 0.55 },
                Aligns = new[] { Align.Left, Align.Right },   // label hugs the right edge, value the left
                Bold = bold,
                Scale = scale,
            };

        /// <summary>Two decimals, Latin digits; the currency only where the slip has room for it.</summary>
        private static string Amount(decimal value, ReceiptInfo info)
            => value.ToString("#,##0.00", En) + (info != null ? " " + Or(info.Currency, "ج.س") : "");

        private static string Or(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
