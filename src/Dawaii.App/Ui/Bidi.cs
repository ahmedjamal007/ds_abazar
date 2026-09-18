namespace Dawaii.App.Ui
{
    /// <summary>
    /// Keeps Latin and numeric runs upright inside Arabic text.
    ///
    /// Everything this app prints is laid out right-to-left, and the Unicode bidirectional algorithm
    /// then reorders any left-to-right run it finds inside a line. For a bare number that is harmless —
    /// digits are neutral enough — but anything with internal punctuation is rearranged around it: a
    /// receipt printed "2026-08-19 22:49" and the customer read "22:49 19-08-2026", with the date's own
    /// pieces swapped. An amount followed by a currency, or a batch number with a dash, goes the same
    /// way.
    ///
    /// Wrapping the run in LEFT-TO-RIGHT MARKs pins it down: the algorithm treats what is between them
    /// as one left-to-right island and places the island as a whole within the Arabic sentence, which is
    /// what a reader expects to see.
    /// </summary>
    public static class Bidi
    {
        private const char Lrm = '‎';

        /// <summary>A date, amount, code or any other left-to-right run, isolated so it prints as typed.</summary>
        public static string Ltr(string value)
            => string.IsNullOrEmpty(value) ? (value ?? "") : Lrm + value + Lrm;
    }
}
