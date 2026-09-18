using System;
using System.Collections.Generic;
using System.Text;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.Core.Printing
{
    /// <summary>Builds the plain-text body of a receipt (testable, printer-agnostic).</summary>
    public static class ReceiptContent
    {
        public static IReadOnlyList<string> BuildLines(Sale sale, ReceiptInfo info)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            info = info ?? new ReceiptInfo();
            int w = Math.Max(20, info.Width);
            var lines = new List<string>();

            lines.Add(Center(info.PharmacyName, w));
            lines.Add(Center("فاتورة بيع", w));
            lines.Add(new string('-', w));
            lines.Add($"رقم: {sale.SaleNumber}");
            lines.Add($"التاريخ: {sale.CreatedAt:yyyy-MM-dd HH:mm}");
            if (!string.IsNullOrEmpty(info.CashierName)) lines.Add($"الكاشير: {info.CashierName}");
            lines.Add($"النوع: {(sale.SaleType == SaleType.Credit ? "آجل" : "نقدي")}");
            lines.Add(new string('-', w));

            foreach (SaleLine line in sale.Lines)
            {
                string name = line.ItemName ?? ("#" + line.ItemId);
                lines.Add(name);
                string qty = $"{line.Quantity} {UnitConverter.LabelAr(line.UnitType)} × {line.UnitPrice * line.UnitsEach:0.##}";
                lines.Add(TwoCols(qty, line.LineTotal.ToString("0.00"), w));
            }

            lines.Add(new string('-', w));
            lines.Add(TwoCols("الإجمالي الفرعي", sale.Subtotal.ToString("0.00"), w));
            if (sale.Discount > 0) lines.Add(TwoCols("الخصم", sale.Discount.ToString("0.00"), w));
            lines.Add(TwoCols("الإجمالي " + info.Currency, sale.Total.ToString("0.00"), w));
            lines.Add(new string('-', w));
            lines.Add(Center(info.Footer, w));
            return lines;
        }

        public static string BuildText(Sale sale, ReceiptInfo info)
            => string.Join(Environment.NewLine, BuildLines(sale, info));

        private static string Center(string s, int w)
        {
            s = s ?? "";
            if (s.Length >= w) return s.Substring(0, w);
            int pad = (w - s.Length) / 2;
            return new string(' ', pad) + s;
        }

        private static string TwoCols(string left, string right, int w)
        {
            left = left ?? ""; right = right ?? "";
            int space = w - left.Length - right.Length;
            if (space < 1) space = 1;
            return left + new string(' ', space) + right;
        }
    }
}
