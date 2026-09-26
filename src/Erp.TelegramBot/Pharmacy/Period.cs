namespace Erp.TelegramBot.Pharmacy;

/// <summary>
/// A stretch of time a manager asked about.
///
/// "week" and "month" are genuinely ambiguous words and the ambiguity matters here, because the
/// answer is money. "This week" depends on which day a week starts — Saturday in Sudan, Monday by
/// ISO, Sunday in much of the world — and "this month" on the 2nd means something very different
/// from "the last month".
///
/// Rather than pick a convention and hope, these are ROLLING windows and every reply states the one
/// it used. A manager reading "آخر 7 أيام" knows what they are looking at; a manager reading "هذا
/// الأسبوع" does not, and may act on a figure that measures two days.
///
/// Calendar periods can be added later if the pharmacy's accounting wants them — but they should be
/// named differently rather than quietly replacing these.
/// </summary>
public readonly struct Period
{
    /// <summary>What to print, so the reply is never ambiguous about what it measured.</summary>
    public string Label { get; }

    public DateTime FromInclusive { get; }

    /// <summary>Exclusive, matching every range query in the ERP.</summary>
    public DateTime ToExclusive { get; }

    /// <summary>For a file name — Latin, no spaces.</summary>
    public string Slug { get; }

    private Period(string label, string slug, DateTime from, DateTime to)
    {
        Label = label;
        Slug = slug;
        FromInclusive = from;
        ToExclusive = to;
    }

    /// <summary>The words accepted, for a usage message.</summary>
    public static string Accepted => "today | week | month";

    /// <summary>
    /// Reads a period word. Accepts the English words from the command, and the obvious Arabic ones
    /// too, because a manager typing on an Arabic keyboard will reach for those first.
    /// </summary>
    public static bool TryParse(string? word, DateTime now, out Period period)
    {
        DateTime today = now.Date;
        string w = (word ?? "").Trim().ToLowerInvariant();

        switch (w)
        {
            case "today":
            case "اليوم":
                period = new Period("اليوم", "today", today, today.AddDays(1));
                return true;

            case "week":
            case "اسبوع":
            case "أسبوع":
                // Seven days INCLUDING today, so "last 7 days" on a Monday covers last Tuesday to now.
                period = new Period("آخر 7 أيام", "7days", today.AddDays(-6), today.AddDays(1));
                return true;

            case "month":
            case "شهر":
                period = new Period("آخر 30 يوماً", "30days", today.AddDays(-29), today.AddDays(1));
                return true;

            default:
                period = default;
                return false;
        }
    }

    /// <summary>The dates, for a file name or a heading.</summary>
    public string Dates =>
        FromInclusive.ToString("yyyy-MM-dd") + "_" + ToExclusive.AddDays(-1).ToString("yyyy-MM-dd");
}
