using System;
using System.Data.Common;
using System.Globalization;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// SQLite storage conventions shared by all repositories:
    ///  * money is stored as REAL — bound as <c>double</c> (so SQL SUM works) and read back rounded to 2dp;
    ///  * timestamps are stored as TEXT "yyyy-MM-dd HH:mm:ss" (local) so range comparisons are exact;
    ///  * booleans are stored as INTEGER 0/1.
    /// </summary>
    internal static class Db
    {
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        // ---- writing ----
        public static object Money(decimal value) => (double)value;
        public static object MoneyN(decimal? value) => value.HasValue ? (object)(double)value.Value : DBNull.Value;
        public static string Time(DateTime value) => value.ToString(TimeFormat, CultureInfo.InvariantCulture);
        public static object Text(string s) => (object)s ?? DBNull.Value;
        public static object IntN(int? v) => v.HasValue ? (object)v.Value : DBNull.Value;

        // ---- reading ----
        public static decimal GetMoney(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            // GetValue yields double (SQLite REAL) or decimal (MySQL DECIMAL); Convert handles both.
            return decimal.Round(Convert.ToDecimal(r.GetValue(i)), 2);
        }

        /// <summary>Reads a per-unit rate WITHOUT rounding to 2 decimals — used for the item's stored
        /// per-single-unit selling price so a box price derived as box/unitsPerBox round-trips exactly
        /// (V1.3: prices are entered by box, strip = box/stripsPerBox).</summary>
        public static decimal GetRate(DbDataReader r, string col)
            => Convert.ToDecimal(r.GetValue(r.GetOrdinal(col)));

        /// <summary>Nullable variant of <see cref="GetRate"/> (items not yet priced store NULL).</summary>
        public static decimal? GetRateN(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? (decimal?)null : Convert.ToDecimal(r.GetValue(i));
        }

        public static int GetInt(DbDataReader r, string col) => Convert.ToInt32(r.GetValue(r.GetOrdinal(col)));
        public static long GetLong(DbDataReader r, string col) => Convert.ToInt64(r.GetValue(r.GetOrdinal(col)));
        public static bool GetBool(DbDataReader r, string col) => Convert.ToInt64(r.GetValue(r.GetOrdinal(col))) != 0;
        public static string GetString(DbDataReader r, string col) => r.GetString(r.GetOrdinal(col));

        public static string GetStringN(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        public static int? GetIntN(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? (int?)null : Convert.ToInt32(r.GetValue(i));
        }

        public static DateTime GetTime(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            object v = r.GetValue(i);
            if (v is DateTime dt) return dt;
            return ParseTime(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        public static DateTime? GetTimeN(DbDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return null;
            object v = r.GetValue(i);
            if (v is DateTime dt) return dt;
            return ParseTime(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static DateTime ParseTime(string s)
        {
            if (DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
                return exact;
            return DateTime.Parse(s, CultureInfo.InvariantCulture); // tolerate default/ISO forms
        }
    }
}
