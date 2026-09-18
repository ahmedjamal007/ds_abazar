using System;

namespace Dawaii.App.Ui
{
    /// <summary>Display formatting for money and dates using the configured currency label.</summary>
    public static class Fmt
    {
        private static string _currency;

        public static string Currency
        {
            get
            {
                if (_currency == null)
                {
                    try { _currency = Session.Services?.Settings.Get("currency"); } catch { }
                    if (string.IsNullOrEmpty(_currency)) _currency = "ج.س";
                }
                return _currency;
            }
        }

        /// <summary>Call after the currency setting changes so the label refreshes.</summary>
        public static void ResetCurrency() => _currency = null;

        public static string Money(decimal amount) => amount.ToString("#,##0.00") + " " + Currency;

        public static string Date(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "—";

        public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm");
    }
}
