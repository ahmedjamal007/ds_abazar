using System;
using System.Collections.Generic;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// The ways a non-credit sale is settled, in one place (V2.3).
    ///
    /// The code is what <c>sales.payment_method</c> stores; the label is what every screen and every
    /// printout shows. Until V2.3 the label lookup was copied into four files and the list of codes
    /// lived in the POS screen alone, so when أوكاش was added there it reached none of the others: the
    /// receipt printed those sales as كاش, the shift report put them in no bucket, and — because the
    /// tuple was entered backwards — the Arabic word went into the database as the code. Everything
    /// now asks here, and <see cref="Normalize"/> reads the rows that were written that way.
    /// </summary>
    public static class PaymentMethods
    {
        public const string Cash = "Cash";
        public const string Bankak = "Bankak";
        public const string Fawry = "Fawry";
        public const string Ocash = "Ocash";

        /// <summary>Every method, in the order the POS offers them and reports list them.</summary>
        public static readonly IReadOnlyList<(string Code, string LabelAr)> All = new[]
        {
            (Cash, "كاش"),
            (Bankak, "بنكك"),
            (Fawry, "فوري"),
            (Ocash, "أوكاش"),
        };

        /// <summary>The Arabic label for a stored code. Unknown or null reads as cash, as it always has.</summary>
        public static string LabelAr(string stored)
        {
            string code = Normalize(stored);
            foreach (var m in All)
                if (m.Code == code) return m.LabelAr;
            return "كاش";
        }

        /// <summary>
        /// The canonical code for whatever a sale row holds. Null was always cash. The Arabic spellings
        /// of أوكاش are the rows written by the POS before the code was corrected; a migration rewrites
        /// them, and this keeps reading them correctly on a database the migration has not reached.
        /// </summary>
        public static string Normalize(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return Cash;
            string s = stored.Trim();

            foreach (var m in All)
                if (string.Equals(s, m.Code, StringComparison.OrdinalIgnoreCase)) return m.Code;

            if (s == "اوكاش" || s == "أوكاش" || string.Equals(s, "o-cash", StringComparison.OrdinalIgnoreCase))
                return Ocash;

            return Cash;
        }
    }
}
