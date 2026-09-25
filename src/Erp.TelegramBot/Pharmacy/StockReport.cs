using System.Globalization;
using System.Text;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;

namespace Erp.TelegramBot.Reports;

/// <summary>
/// Turning stock into something readable on a phone.
///
/// Pure text in, pure text out — no Telegram types, no database — so every line of wording is
/// assertable in a test. That matters more than it sounds: this is the bot's entire product. A
/// manager acts on these numbers without being able to see the shelf.
///
/// Quantities are reported in BOXES AND STRIPS, never in the single tablets the database stores.
/// "1,247 tablets" is a number nobody can picture; "12 boxes + 4 strips" is a thing you can walk
/// over and look at.
/// </summary>
public static class StockReport
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>
    /// The answer to /stock for one drug: what it is, how much is on the shelf, whether that is below
    /// the reorder level, and which batches make it up with their expiry dates.
    /// </summary>
    public static string One(StockDetail detail)
    {
        Item item = detail.Item;
        var sb = new StringBuilder();

        sb.Append(item.DisplayName).Append('\n');
        if (!string.IsNullOrWhiteSpace(item.GenericName) &&
            !string.Equals(item.GenericName, item.NameEn, StringComparison.OrdinalIgnoreCase))
            sb.Append(item.GenericName).Append('\n');

        sb.Append('\n');

        PackQuantity have = PackQuantity.Of(item, detail.AvailableUnits);
        sb.Append("المتوفر: ").Append(Describe(have, item)).Append('\n');

        // The reorder level is stored in single units; shown the same way as the quantity so the two
        // can actually be compared by eye.
        if (item.MinQuantity > 0)
        {
            PackQuantity reorder = PackQuantity.Of(item, item.MinQuantity);
            sb.Append("حد الطلب: ").Append(Describe(reorder, item)).Append('\n');
        }

        if (detail.AvailableUnits <= 0)
            sb.Append("\n⚠️ نفد من المخزون.\n");
        else if (item.MinQuantity > 0 && detail.AvailableUnits <= item.MinQuantity)
            sb.Append("\n⚠️ عند حد الطلب أو أقل.\n");

        // Packaging, so "4 strips" means something to somebody who does not have the box in hand.
        if (item.UnitsPerStrip > 0 && item.StripsPerBox > 0)
            sb.Append('\n').Append("العلبة: ").Append(item.StripsPerBox.ToString(En))
              .Append(" شريط × ").Append(item.UnitsPerStrip.ToString(En)).Append(" حبة\n");

        sb.Append('\n').Append(Batches(detail));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The batch breakdown.
    ///
    /// This pharmacy has no warehouses — the ERP has never had the concept — so the useful breakdown
    /// is by BATCH: lot number and expiry date. It is also the more valuable answer, because it tells
    /// a manager not just how much they have but how much of it is about to be unsellable. Ordered
    /// soonest-to-expire, which is the order the till sells them in.
    /// </summary>
    private static string Batches(StockDetail detail)
    {
        if (detail.Batches.Count == 0)
            return detail.AvailableUnits > 0
                ? "لا توجد تفاصيل دفعات."
                : "لا توجد دفعات على الرف.";

        var sb = new StringBuilder("الدفعات (الأقرب انتهاءً أولاً):\n");
        DateTime today = DateTime.Today;

        foreach (BatchLine batch in detail.Batches)
        {
            PackQuantity q = PackQuantity.Of(batch.UnitsPerStrip, batch.StripsPerBox, batch.Units);

            sb.Append("• ").Append(Describe(q, batch.UnitsPerStrip, batch.StripsPerBox));

            if (batch.Expiry.HasValue)
            {
                int days = (int)(batch.Expiry.Value.Date - today).TotalDays;
                sb.Append(" — ").Append(batch.Expiry.Value.ToString("yyyy-MM-dd", En));

                // The part a manager actually reacts to. Stock that expires next month is not the
                // same asset as stock that expires in two years, and a total hides the difference.
                if (days < 0) sb.Append(" (منتهية)");
                else if (days == 0) sb.Append(" (تنتهي اليوم)");
                else if (days <= 90) sb.Append(" (").Append(days.ToString(En)).Append(" يوم)");
            }
            else sb.Append(" — بدون تاريخ انتهاء");

            if (!string.IsNullOrWhiteSpace(batch.BatchNumber))
                sb.Append("  [").Append(batch.BatchNumber).Append(']');

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>When a name matched several drugs, ask rather than guess which one was meant.</summary>
    public static string Ambiguous(string query, IReadOnlyList<Item> matches)
    {
        var sb = new StringBuilder("أكثر من صنف يطابق \"");
        sb.Append(query).Append("\":\n\n");

        foreach (Item item in matches)
            sb.Append("• ").Append(item.DisplayName).Append('\n');

        sb.Append("\nاكتب اسماً أدق، أو امسح الباركود.");
        return sb.ToString();
    }

    public static string NotFound(string query)
        => "لا يوجد صنف بهذا الاسم أو الباركود: \"" + query + "\"";

    public static string Usage =>
        "الاستخدام: /stock ثم الباركود أو اسم الصنف\n" +
        "مثال: /stock panadol";

    // ---------------- wording ----------------

    private static string Describe(PackQuantity q, Item item)
        => Describe(q, item.UnitsPerStrip, item.StripsPerBox);

    /// <summary>
    /// "12 علبة + 4 شريط", or just the parts that exist.
    ///
    /// Only mentions a level of packaging the data records — an item with no strip size recorded is
    /// reported in plain units rather than having packaging invented for it. The exact unit total is
    /// always appended in brackets, because it is the number that reconciles with every other screen
    /// in the program and somebody will eventually need to check.
    /// </summary>
    private static string Describe(PackQuantity q, int unitsPerStrip, int stripsPerBox)
    {
        if (q.TotalUnits <= 0) return "صفر";

        var parts = new List<string>();
        if (q.Boxes > 0) parts.Add(q.Boxes.ToString(En) + " علبة");
        if (q.Strips > 0) parts.Add(q.Strips.ToString(En) + " شريط");
        if (q.Units > 0) parts.Add(q.Units.ToString(En) + " حبة");

        // Everything landed in one bucket and there is no packaging to elaborate on.
        if (parts.Count == 0) parts.Add(q.TotalUnits.ToString(En) + " حبة");

        string described = string.Join(" + ", parts);

        // No point restating the total when the description already is the total.
        bool worthTheTotal = q.Boxes > 0 || q.Strips > 0;
        return worthTheTotal
            ? described + "  (" + q.TotalUnits.ToString("N0", En) + " حبة)"
            : described;
    }
}
