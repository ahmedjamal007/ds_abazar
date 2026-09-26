using System.Globalization;
using System.Text;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;

namespace Erp.TelegramBot.Reports;

/// <summary>
/// The answer to /sales, and the files behind /report.
///
/// Every reply states the window it measured. "week" and "month" are ambiguous words and the answer
/// is money, so a manager must never have to guess whether a figure covers seven days or two.
/// </summary>
public static class SalesReport
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>The /sales summary.</summary>
    public static string Summary(DailyReport report, Period period)
    {
        var sb = new StringBuilder();
        sb.Append("المبيعات — ").Append(period.Label).Append('\n');
        sb.Append(period.FromInclusive.ToString("yyyy-MM-dd", En))
          .Append(" إلى ")
          .Append(period.ToExclusive.AddDays(-1).ToString("yyyy-MM-dd", En)).Append('\n').Append('\n');

        if (report.TransactionCount == 0)
        {
            sb.Append("لا مبيعات في هذه الفترة.");
            return sb.ToString();
        }

        sb.Append("عدد الفواتير: ").Append(report.TransactionCount.ToString(En)).Append('\n');
        sb.Append("إجمالي المبيعات: ").Append(Money(report.TotalSales)).Append('\n');
        sb.Append("نقدي: ").Append(Money(report.CashTotal)).Append('\n');
        sb.Append("آجل: ").Append(Money(report.CreditTotal)).Append('\n');

        if (report.ReturnedCount > 0)
            sb.Append("مرتجعات: ").Append(report.ReturnedCount.ToString(En))
              .Append(" فاتورة بمبلغ ").Append(Money(report.ReturnedTotal)).Append('\n');

        // Profit is included by the ERP only for an administrator. The bot does not decide this and
        // does not second-guess it — if the flag is off, the figure is simply not shown.
        if (report.ProfitVisible)
            sb.Append("الأرباح: ").Append(Money(report.TotalProfit)).Append('\n');

        sb.Append('\n').Append("للملف الكامل: /report sales ").Append(SlugOf(period));
        return sb.ToString();
    }

    public static string Usage =>
        "الاستخدام: /sales ثم الفترة\n" +
        "الفترات: " + Period.Accepted + "\n" +
        "مثال: /sales week";

    // ---------------- the files ----------------

    /// <summary>Every invoice in the period, with the totals on top.</summary>
    public static OutgoingFile SalesCsv(DailyReport totals, IReadOnlyList<Sale> invoices, Period period)
    {
        var summary = new CsvSection { Heading = "الملخص", Columns = ["البند", "القيمة"] };
        summary.Add("الفترة", period.Label);
        summary.Add("من", ReportFile.Date(period.FromInclusive));
        summary.Add("إلى", ReportFile.Date(period.ToExclusive.AddDays(-1)));
        summary.Add("عدد الفواتير", ReportFile.Int(totals.TransactionCount));
        summary.Add("إجمالي المبيعات", ReportFile.Money(totals.TotalSales));
        summary.Add("نقدي", ReportFile.Money(totals.CashTotal));
        summary.Add("آجل", ReportFile.Money(totals.CreditTotal));
        summary.Add("عدد المرتجعات", ReportFile.Int(totals.ReturnedCount));
        summary.Add("مبلغ المرتجعات", ReportFile.Money(totals.ReturnedTotal));
        if (totals.ProfitVisible)
            summary.Add("الأرباح", ReportFile.Money(totals.TotalProfit));

        var rows = new CsvSection
        {
            Heading = "الفواتير",
            Columns = ["رقم الفاتورة", "التاريخ", "النوع", "طريقة الدفع", "الإجمالي", "المرتجع", "الصافي"]
        };

        foreach (Sale sale in invoices.OrderBy(s => s.CreatedAt))
            rows.Add(
                ReportFile.Int(sale.SaleNumber),
                ReportFile.DateTime(sale.CreatedAt),
                sale.SaleType == SaleType.Credit ? "آجل" : "نقدي",
                sale.SaleType == SaleType.Credit ? "" : PaymentMethods.LabelAr(sale.PaymentMethod),
                ReportFile.Money(sale.Total),
                ReportFile.Money(sale.RefundedTotal),
                ReportFile.Money(sale.NetTotal));

        return ReportFile.Csv(
            "sales_" + period.Dates + ".csv", "تقرير المبيعات — " + period.Label, [summary, rows]);
    }

    /// <summary>The whole low-stock list, which /low can only show a page of.</summary>
    public static OutgoingFile LowStockCsv(IReadOnlyList<LowStockLine> low, DateTime now)
    {
        var section = new CsvSection
        {
            Heading = "أصناف عند حد الطلب أو أقل: " + low.Count.ToString(En),
            Columns = ["الصنف", "الاسم العلمي", "المتوفر (حبة)", "حد الطلب (حبة)", "علبة", "شريط", "حبة"]
        };

        foreach (LowStockLine line in low)
        {
            PackQuantity q = PackQuantity.Of(line.Item, line.AvailableUnits);
            section.Add(
                line.Item.DisplayName,
                line.Item.GenericName ?? "",
                ReportFile.Int(line.AvailableUnits),
                ReportFile.Int(line.ReorderLevel),
                ReportFile.Int(q.Boxes),
                ReportFile.Int(q.Strips),
                ReportFile.Int(q.Units));
        }

        return ReportFile.Csv(
            "low_stock_" + ReportFile.Date(now) + ".csv",
            "تقرير المخزون المنخفض — " + ReportFile.Date(now), [section]);
    }

    public static OutgoingFile BestSellersCsv(IReadOnlyList<BestSellerRow> rows, Period period)
    {
        var section = new CsvSection
        {
            Heading = "الأكثر مبيعاً",
            Columns = ["الصنف", "الكمية المباعة (حبة)", "الإيراد"]
        };

        foreach (BestSellerRow row in rows)
            section.Add(row.Name ?? "", ReportFile.Int(row.UnitsSold), ReportFile.Money(row.Revenue));

        return ReportFile.Csv(
            "best_sellers_" + period.Dates + ".csv",
            "الأكثر مبيعاً — " + period.Label, [section]);
    }

    public static OutgoingFile DeadStockCsv(IReadOnlyList<DeadStockRow> rows, int days, DateTime now)
    {
        var section = new CsvSection
        {
            Heading = "لم يُبَع خلال " + days.ToString(En) + " يوم",
            Columns = ["الصنف", "الاسم العلمي", "المتوفر (حبة)", "قيمة التكلفة"]
        };

        foreach (DeadStockRow row in rows)
            // Cost value, not selling value: money already spent and sitting on a shelf is the
            // figure that makes dead stock worth acting on.
            section.Add(
                row.Item?.DisplayName ?? "",
                row.Item?.GenericName ?? "",
                ReportFile.Int(row.AvailableUnits),
                ReportFile.Money((row.Item?.PurchasePrice ?? 0m) * row.AvailableUnits));

        return ReportFile.Csv(
            "dead_stock_" + ReportFile.Date(now) + ".csv",
            "المخزون الراكد — " + days.ToString(En) + " يوم", [section]);
    }

    private static string Money(decimal value) => value.ToString("N2", En);

    private static string SlugOf(Period period) => period.Slug switch
    {
        "today" => "today",
        "7days" => "week",
        _ => "month",
    };
}
