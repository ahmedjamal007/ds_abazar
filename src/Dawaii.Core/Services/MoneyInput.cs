using System.Globalization;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Reading an amount of money that somebody typed (V2.5).
    ///
    /// There were four copies of this, and they did not agree. Two screens tried the machine's culture
    /// and then fell back to the invariant one; two called <c>decimal.TryParse</c> bare and took
    /// whatever the culture happened to do. A pharmacist typing the same "1250.50" into a stock form
    /// and into a debt repayment could therefore have it accepted in one and refused in the other.
    ///
    /// This matters more than it looks, and it is about to matter more still. On .NET Framework the
    /// number formats come from Windows NLS; on modern .NET they come from ICU, and the two disagree
    /// about Arabic. Under ar-SD, ICU says the decimal separator is ٫ (U+066B) and the group separator
    /// is ٬ (U+066C) — so a bare culture-sensitive parse of "1250.50" starts returning FALSE on a
    /// machine where it used to work. That is a customer's repayment silently refused, or worse, a
    /// figure read as a different number entirely.
    ///
    /// So the rule here is deliberately generous and is the ONLY rule: accept what the machine's
    /// culture says, accept the plain "1234.56" form whatever the culture, and accept Arabic-Indic
    /// digits, because a keyboard set to Arabic produces them and the person typing does not think of
    /// them as a different number.
    /// </summary>
    public static class MoneyInput
    {
        /// <summary>A lone decimal mark and nothing else — no thousands separators to be ambiguous about.</summary>
        private const NumberStyles Simple = NumberStyles.Float;

        /// <summary>Grouped as well, for a figure copied out of a report.</summary>
        private const NumberStyles Grouped = NumberStyles.Any;

        /// <summary>
        /// Reads a typed amount. Returns false for anything that is not a number — including a blank
        /// box and a half-typed one ("-", "1.") — leaving <paramref name="value"/> at zero.
        ///
        /// The ORDER below is the whole point, and it is not the obvious one.
        ///
        /// Allowing thousands separators makes "1250.50" ambiguous: under de-DE — and any culture that
        /// groups with a dot — a permissive parse reads it as 125050, a hundred times the amount, and
        /// returns true while doing it. The pharmacy's own stock forms had that bug. A hundredfold
        /// silent error on a price is far worse than refusing to read a figure at all.
        ///
        /// So the unambiguous forms go first, with grouping disallowed: the plain "1234.56" this
        /// program itself prints, then whatever single decimal mark the machine's culture uses. Only
        /// once both of those have failed is grouping allowed, by which point the remaining text has
        /// to contain two different separators and cannot be misread.
        /// </summary>
        public static bool TryParse(string text, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string cleaned = Normalize(text);
            CultureInfo local = CultureInfo.CurrentCulture;

            // "1234.56" — what every receipt and price list out of this program looks like.
            if (decimal.TryParse(cleaned, Simple, CultureInfo.InvariantCulture, out value)) return true;

            // "1234,56" on a machine that writes it that way.
            if (decimal.TryParse(cleaned, Simple, local, out value)) return true;

            // "1.234,56" / "1,234.56" — both separators present, so neither can be mistaken.
            if (decimal.TryParse(cleaned, Grouped, local, out value)) return true;
            return decimal.TryParse(cleaned, Grouped, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Reads a typed amount, or returns <paramref name="fallback"/> when it is not a number.</summary>
        public static decimal Or(string text, decimal fallback)
        {
            decimal value;
            return TryParse(text, out value) ? value : fallback;
        }

        /// <summary>
        /// Turns Arabic-Indic digits into the ones <c>decimal.TryParse</c> understands, and the Arabic
        /// decimal and thousands marks into plain ones.
        ///
        /// An Arabic keyboard produces ٠١٢٣٤٥٦٧٨٩ and the pharmacist typing them is not entering a
        /// different number. Neither parse would take them, so without this a perfectly good amount is
        /// simply refused with no explanation of what is wrong with it.
        /// </summary>
        private static string Normalize(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.Trim())
            {
                if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠')));       // ٠-٩
                else if (c >= '۰' && c <= '۹') sb.Append((char)('0' + (c - '۰')));  // ۰-۹ (Persian)
                else if (c == '٫') sb.Append('.');      // ٫ Arabic decimal separator
                else if (c == '٬') sb.Append(',');      // ٬ Arabic thousands separator
                else if (c == '‏' || c == '‎') { } // stray RTL/LTR marks from a paste
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
