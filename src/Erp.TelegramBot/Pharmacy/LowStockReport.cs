using System.Globalization;
using System.Text;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;

namespace Erp.TelegramBot.Reports;

/// <summary>One page of the low-stock list, plus what the buttons under it should say.</summary>
/// <param name="Text">The message body.</param>
/// <param name="PageIndex">Zero-based.</param>
/// <param name="PageCount">Total pages; 1 when everything fits.</param>
public sealed record LowStockPage(string Text, int PageIndex, int PageCount)
{
    public bool HasPrevious => PageIndex > 0;
    public bool HasNext => PageIndex < PageCount - 1;
}

/// <summary>
/// The answer to /low: every drug at or below its reorder level.
///
/// Paginated, and not for tidiness. Telegram refuses any message over 4096 characters, so a pharmacy
/// with sixty drugs below their threshold would get NOTHING — the send fails and the manager sees a
/// bot that ignored them. A page size small enough to be safe, with buttons to walk through, is the
/// difference between a working feature and one that breaks precisely at the pharmacies that need it
/// most.
///
/// Pure text, so the page arithmetic and the wording are both testable with no Telegram involved.
/// </summary>
public static class LowStockReport
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>
    /// Items per page.
    ///
    /// Chosen against the 4096-character limit rather than by eye: each line is at most a couple of
    /// hundred characters with a long Arabic name and a packaged quantity, so ten leaves a wide
    /// margin under the limit even in the worst case. <c>FitsTelegram</c> in the tests holds this
    /// honest.
    /// </summary>
    public const int PageSize = 10;

    public static LowStockPage Page(IReadOnlyList<LowStockLine> all, int pageIndex)
    {
        if (all.Count == 0)
            return new LowStockPage(
                "لا يوجد صنف عند حد الطلب أو أقل.\nالمخزون بحالة جيدة.", 0, 1);

        int pageCount = (all.Count + PageSize - 1) / PageSize;

        // Clamp rather than throw. A stale button from an older, longer list is a normal thing to
        // receive — the manager left the message in their chat and pressed it an hour later.
        int page = pageIndex < 0 ? 0 : pageIndex >= pageCount ? pageCount - 1 : pageIndex;

        var sb = new StringBuilder();
        sb.Append("أصناف عند حد الطلب أو أقل: ").Append(all.Count.ToString(En));
        if (pageCount > 1)
            sb.Append("   (صفحة ").Append((page + 1).ToString(En))
              .Append(" من ").Append(pageCount.ToString(En)).Append(')');
        sb.Append("\n\n");

        int outOfStock = all.Count(l => l.IsOut);
        if (outOfStock > 0 && page == 0)
            sb.Append("⚠️ ").Append(outOfStock.ToString(En)).Append(" منها نفد تماماً.\n\n");

        foreach (LowStockLine line in all.Skip(page * PageSize).Take(PageSize))
        {
            sb.Append(line.IsOut ? "⛔ " : "• ").Append(line.Item.DisplayName).Append('\n');
            sb.Append("   المتوفر: ").Append(Quantity(line));

            if (line.ReorderLevel > 0)
            {
                PackQuantity reorder = PackQuantity.Of(line.Item, line.ReorderLevel);
                sb.Append("  /  حد الطلب: ").Append(Short(reorder, line.Item.UnitsPerStrip, line.Item.StripsPerBox));
            }
            sb.Append('\n');
        }

        if (pageCount > 1)
            sb.Append("\nاستخدم الأزرار للتنقل بين الصفحات.");

        return new LowStockPage(sb.ToString().TrimEnd(), page, pageCount);
    }

    public static string Usage => "الاستخدام: /low — الأصناف عند حد الطلب أو أقل";

    private static string Quantity(LowStockLine line)
    {
        if (line.IsOut) return "صفر";
        PackQuantity q = PackQuantity.Of(line.Item, line.AvailableUnits);
        return Short(q, line.Item.UnitsPerStrip, line.Item.StripsPerBox);
    }

    /// <summary>
    /// A compact quantity for a list line. Shorter than the /stock wording on purpose: ten of these
    /// share one message, and the unit total in brackets on every line would push the page towards
    /// the limit this class exists to stay under.
    /// </summary>
    private static string Short(PackQuantity q, int unitsPerStrip, int stripsPerBox)
    {
        if (q.TotalUnits <= 0) return "صفر";

        var parts = new List<string>();
        if (q.Boxes > 0) parts.Add(q.Boxes.ToString(En) + " علبة");
        if (q.Strips > 0) parts.Add(q.Strips.ToString(En) + " شريط");
        if (q.Units > 0) parts.Add(q.Units.ToString(En) + " حبة");

        return parts.Count > 0 ? string.Join(" + ", parts) : q.TotalUnits.ToString(En) + " حبة";
    }
}
