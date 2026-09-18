using System;
using System.Collections.Generic;
using System.Text;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Reads an invoice number out of whatever the cashier typed.
    ///
    /// The invoice number is a plain integer, so the normal case is simply parsing it. The one wrinkle
    /// is paper already in circulation: receipts printed before V1.8 carry the old composite number
    /// "yyyyMMdd-id", which these right-to-left screens render reversed ("7-20260801") and staff copy
    /// down in that order without the dash. Both readings still resolve to the invoice's id, so a
    /// customer walking in with an old receipt can still be served.
    /// </summary>
    public static class SaleNumberParser
    {
        private const int DateLength = 8;   // yyyyMMdd

        /// <summary>
        /// Invoice numbers worth looking up for this input, most likely first. Empty when the input
        /// holds no number at all.
        /// </summary>
        public static IReadOnlyList<int> Candidates(string raw)
        {
            var result = new List<int>();
            string digits = DigitsOnly(raw);
            if (digits.Length == 0) return result;

            Add(result, digits);                                  // the invoice number as printed today

            // Legacy "yyyyMMdd-id", typed in either direction: whichever end is a date, the rest is the id.
            if (digits.Length > DateLength)
            {
                if (LooksLikeDate(digits.Substring(0, DateLength)))
                    Add(result, digits.Substring(DateLength));
                if (LooksLikeDate(digits.Substring(digits.Length - DateLength)))
                    Add(result, digits.Substring(0, digits.Length - DateLength));
            }
            return result;
        }

        /// <summary>The digits of the input with Arabic-Indic numerals folded to ASCII.</summary>
        public static string DigitsOnly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));   // ٠..٩
                else if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));   // ۰..۹
            }
            return sb.ToString();
        }

        /// <summary>An 8-digit run that could be the yyyyMMdd stamp of an old invoice number.</summary>
        private static bool LooksLikeDate(string digits)
        {
            if (digits.Length != DateLength || !int.TryParse(digits, out int value)) return false;
            int year = value / 10000, month = value / 100 % 100, day = value % 100;
            return year >= 2000 && year <= 2099 && month >= 1 && month <= 12 && day >= 1 && day <= 31;
        }

        private static void Add(List<int> list, string digits)
        {
            // Anything too long to be an id is not an invoice number — int.TryParse says so for free.
            if (!int.TryParse(digits, out int number) || number <= 0) return;
            if (!list.Contains(number)) list.Add(number);
        }
    }
}
