using System.Globalization;
using System.Text;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;

namespace Erp.TelegramBot.Reports;

/// <summary>
/// The owner's reports, worded for somebody reading a phone rather than a dashboard.
///
/// Not one figure is calculated here. The payment split arrives from ShiftReconciliation, debts from
/// the customer and supplier services, expiry from InventoryService, prices from UnitConverter —
/// every number is the Admin module's own. This file decides only how to say it.
///
/// That division matters more than it looks: a total on the owner's phone that disagreed with the
/// total on the pharmacy's screen would not be a display bug, it would be two sources of truth about
/// money, and nobody would know which to believe.
/// </summary>
public static class OwnerReports
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    private static string Money(decimal value) => value.ToString("N2", En);
    private static string Time(DateTime value) => value.ToString("HH:mm", En);
    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", En);

    // ---------------- sales ----------------

    /// <summary>Takings, broken down the way the money actually arrived.</summary>
    public static string Takings(TakingsLine t, Period period)
    {
        var sb = new StringBuilder();
        sb.Append("📊 المبيعات — ").Append(period.Label).Append('\n');
        sb.Append(Date(period.FromInclusive));
        if (period.ToExclusive.AddDays(-1).Date != period.FromInclusive.Date)
            sb.Append(" إلى ").Append(Date(period.ToExclusive.AddDays(-1)));
        sb.Append('\n').Append('\n');

        if (t.InvoiceCount == 0)
        {
            sb.Append("لا توجد مبيعات في هذه الفترة.");
            return sb.ToString();
        }

        sb.Append(Channels(t));
        sb.Append("─────────────\n");
        sb.Append("الإجمالي: ").Append(Money(t.GrandTotal)).Append('\n');
        sb.Append("عدد الفواتير: ").Append(t.InvoiceCount.ToString(En)).Append('\n');

        if (t.ReturnedCount > 0)
            sb.Append("مرتجعات: ").Append(t.ReturnedCount.ToString(En))
              .Append(" بمبلغ ").Append(Money(t.ReturnedTotal)).Append('\n');

        if (t.ProfitVisible)
            sb.Append("الأرباح: ").Append(Money(t.Profit)).Append('\n');

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The four channels plus credit, always in the same order and always all of them — including
    /// the zeroes. A missing line reads as missing data; a zero reads as "none today", which is the
    /// answer the owner actually wanted.
    /// </summary>
    private static string Channels(TakingsLine t)
    {
        var sb = new StringBuilder();
        sb.Append("نقدي: ").Append(Money(t.Till.Cash)).Append('\n');
        sb.Append("بنكك: ").Append(Money(t.Till.Bankak)).Append('\n');
        sb.Append("فوري: ").Append(Money(t.Till.Fawry)).Append('\n');
        sb.Append("أوكاش: ").Append(Money(t.Till.Ocash)).Append('\n');
        sb.Append("آجل: ").Append(Money(t.CreditTotal)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Each employee gets the same five-channel breakdown the pharmacy gets, so a total can be read
    /// against the till they were standing at.
    ///
    /// Capped like the other lists: each employee is a seven-line block, so a pharmacy with years of
    /// staff accounts would otherwise build a message Telegram refuses outright — the owner would see
    /// nothing at all rather than a long report. The reader sorts by takings descending, so what
    /// survives the cap is who sold the most, and the pharmacy total below counts everybody.
    /// </summary>
    public static string ByEmployee(IReadOnlyList<EmployeeTakings> staff, Period period, int show)
    {
        if (staff.Count == 0)
            return "📊 المبيعات حسب الموظف — " + period.Label + "\n\nلا توجد مبيعات في هذه الفترة.";

        var sb = new StringBuilder();
        sb.Append("📊 المبيعات حسب الموظف — ").Append(period.Label).Append('\n').Append('\n');

        foreach (EmployeeTakings e in staff.Take(show))
        {
            sb.Append("👤 ").Append(e.Name).Append('\n');
            sb.Append(Channels(e.Takings));
            sb.Append("الإجمالي: ").Append(Money(e.Takings.GrandTotal))
              .Append("   (").Append(e.Takings.InvoiceCount.ToString(En)).Append(" فاتورة)\n");
            sb.Append('\n');
        }

        if (staff.Count > show)
            sb.Append("… و").Append((staff.Count - show).ToString(En)).Append(" موظفاً آخر.\n\n");

        sb.Append("─────────────\n");
        sb.Append("إجمالي الصيدلية: ")
          .Append(Money(staff.Sum(e => e.Takings.GrandTotal)));

        return sb.ToString();
    }

    // ---------------- shifts ----------------

    public static string Shifts(IReadOnlyList<ShiftLine> shifts, DateTime day, int show)
    {
        if (shifts.Count == 0)
            return "🕐 الورديات — " + Date(day) + "\n\nلا يوجد نشاط في هذا اليوم.";

        var sb = new StringBuilder();
        sb.Append("🕐 تقرير الورديات — ").Append(Date(day)).Append('\n').Append('\n');

        foreach (ShiftLine shift in shifts.Take(show))
        {
            sb.Append("👤 ").Append(shift.Name).Append('\n');

            sb.Append("الحضور: ")
              .Append(shift.FirstLoginAt.HasValue ? Time(shift.FirstLoginAt.Value) : "—");

            // Said as "last transaction", never as a leaving time. The system records logins and no
            // logouts at all, so an end time would be an invention — and this one gets read by an
            // owner deciding whether somebody left early.
            sb.Append("   |   آخر عملية: ")
              .Append(shift.LastSaleAt.HasValue ? Time(shift.LastSaleAt.Value) : "—")
              .Append('\n');

            sb.Append(Channels(new TakingsLine(shift.Till, 0m, shift.SalesCount, 0, 0m, false, 0m))
                .Replace("آجل: 0.00\n", ""));      // credit is reported per employee elsewhere

            sb.Append("عدد الفواتير: ").Append(shift.SalesCount.ToString(En)).Append('\n');
            if (shift.MoneyExpenses > 0)
                sb.Append("سحب نقدي: ").Append(Money(shift.MoneyExpenses)).Append('\n');

            sb.Append('\n');
        }

        if (shifts.Count > show)
            sb.Append("… و").Append((shifts.Count - show).ToString(En)).Append(" وردية أخرى.\n\n");

        sb.Append("ℹ️ وقت الانتهاء غير مسجّل في النظام؛ يُعرض وقت آخر عملية بدلاً منه.");
        return sb.ToString();
    }

    // ---------------- customers and suppliers ----------------

    public static string CustomerDebts(IReadOnlyList<Customer> customers, decimal total, int show)
    {
        if (customers.Count == 0)
            return "👥 مديونية العملاء\n\nلا يوجد عملاء عليهم مديونية.";

        var sb = new StringBuilder();
        sb.Append("👥 مديونية العملاء\n\n");
        sb.Append("الإجمالي المستحق: ").Append(Money(total)).Append('\n');
        sb.Append("عدد العملاء: ").Append(customers.Count.ToString(En)).Append('\n').Append('\n');

        foreach (Customer c in customers.Take(show))
        {
            sb.Append("• ").Append(c.Name).Append(" — ").Append(Money(c.Balance));
            if (!string.IsNullOrWhiteSpace(c.Phone)) sb.Append("   ☎ ").Append(c.Phone);
            sb.Append('\n');
        }

        if (customers.Count > show)
            sb.Append("… و").Append((customers.Count - show).ToString(En)).Append(" غيرهم.\n");

        return sb.ToString().TrimEnd();
    }

    public static string SupplierDebts(IReadOnlyList<Supplier> suppliers, decimal total, int show)
    {
        if (suppliers.Count == 0)
            return "🏢 مديونية الموردين\n\nلا توجد مبالغ مستحقة للموردين.";

        var sb = new StringBuilder();
        sb.Append("🏢 مديونية الموردين\n\n");
        sb.Append("الإجمالي المستحق علينا: ").Append(Money(total)).Append('\n');
        sb.Append("عدد الشركات: ").Append(suppliers.Count.ToString(En)).Append('\n').Append('\n');

        foreach (Supplier s in suppliers.Take(show))
            sb.Append("• ").Append(s.Name).Append(" — ").Append(Money(s.Outstanding))
              .Append("   (").Append(s.InvoiceCount.ToString(En)).Append(" فاتورة)\n");

        if (suppliers.Count > show)
            sb.Append("… و").Append((suppliers.Count - show).ToString(En)).Append(" غيرها.\n");

        return sb.ToString().TrimEnd();
    }

    public static string Customers(IReadOnlyList<Customer> customers, int show)
    {
        if (customers.Count == 0) return "👥 العملاء\n\nلا يوجد عملاء مسجّلون.";

        var sb = new StringBuilder("👥 العملاء — ");
        sb.Append(customers.Count.ToString(En)).Append('\n').Append('\n');

        foreach (Customer c in customers.Take(show))
        {
            sb.Append("• ").Append(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Phone)) sb.Append("   ☎ ").Append(c.Phone);
            if (c.Balance > 0) sb.Append("   — عليه ").Append(Money(c.Balance));
            sb.Append('\n');
        }

        if (customers.Count > show)
            sb.Append("… و").Append((customers.Count - show).ToString(En)).Append(" غيرهم.");

        return sb.ToString().TrimEnd();
    }

    public static string Suppliers(IReadOnlyList<Supplier> suppliers, int show)
    {
        if (suppliers.Count == 0) return "🏢 الموردون\n\nلا توجد شركات مسجّلة.";

        var sb = new StringBuilder("🏢 الموردون — ");
        sb.Append(suppliers.Count.ToString(En)).Append('\n').Append('\n');

        foreach (Supplier s in suppliers.Take(show))
        {
            sb.Append("• ").Append(s.Name);
            if (s.Outstanding > 0) sb.Append("   — مستحق ").Append(Money(s.Outstanding));
            sb.Append('\n');
        }

        if (suppliers.Count > show)
            sb.Append("… و").Append((suppliers.Count - show).ToString(En)).Append(" غيرها.");

        return sb.ToString().TrimEnd();
    }

    // ---------------- purchases ----------------

    public static string Purchases(IReadOnlyList<PurchaseLine> purchases, Period period, int show)
    {
        if (purchases.Count == 0)
            return "🚚 المشتريات — " + period.Label + "\n\nلا توجد فواتير في هذه الفترة.";

        var sb = new StringBuilder();
        sb.Append("🚚 المشتريات — ").Append(period.Label).Append('\n').Append('\n');
        sb.Append("عدد الفواتير: ").Append(purchases.Count.ToString(En)).Append('\n');
        sb.Append("الإجمالي: ").Append(Money(purchases.Sum(p => p.Invoice.Total)))
          .Append('\n').Append('\n');

        foreach (PurchaseLine p in purchases.Take(show))
        {
            PurchaseInvoice i = p.Invoice;
            sb.Append("🧾 ")
              .Append(string.IsNullOrWhiteSpace(i.InvoiceNumber) ? "#" + i.Id : i.InvoiceNumber)
              .Append(" — ").Append(i.SupplierName ?? "—").Append('\n');
            sb.Append("   التاريخ: ").Append(Date(i.InvoiceDate));
            if (!string.IsNullOrWhiteSpace(i.Representative))
                sb.Append("   |   المندوب: ").Append(i.Representative);
            sb.Append('\n');
            sb.Append("   الإجمالي: ").Append(Money(i.Total));
            if (i.Outstanding > 0) sb.Append("   |   المتبقي: ").Append(Money(i.Outstanding));
            sb.Append('\n');
            // Who entered it, by full name — the repository already prefers it over the username.
            sb.Append("   أدخلها: ").Append(p.EnteredBy)
              .Append("   (").Append(i.CreatedAt.ToString("yyyy-MM-dd HH:mm", En)).Append(")\n");
            sb.Append('\n');
        }

        if (purchases.Count > show)
            sb.Append("… و").Append((purchases.Count - show).ToString(En)).Append(" فاتورة أخرى.");

        return sb.ToString().TrimEnd();
    }

    // ---------------- expiry ----------------

    public static string Expiring(IReadOnlyList<NearExpiryRow> rows, int show)
    {
        if (rows.Count == 0) return "📅 قرب الانتهاء\n\nلا توجد أصناف قاربت على الانتهاء.";

        int expired = rows.Count(r => r.IsExpired);

        var sb = new StringBuilder("📅 أصناف قاربت على الانتهاء — ");
        sb.Append(rows.Count.ToString(En)).Append('\n');
        if (expired > 0)
            sb.Append("⛔ ").Append(expired.ToString(En)).Append(" منها منتهية بالفعل.\n");
        sb.Append("القيمة المعرّضة: ").Append(Money(rows.Sum(r => r.ValueAtRisk))).Append('\n').Append('\n');

        foreach (NearExpiryRow r in rows.Take(show))
        {
            PackQuantity q = PackQuantity.Of(r.Item, r.Batch.QuantityUnits);

            sb.Append(r.IsExpired ? "⛔ " : "• ").Append(r.Item.DisplayName).Append('\n');
            sb.Append("   ").Append(Quantity(q));
            if (r.Batch.ExpiryDate.HasValue)
                sb.Append("   |   ").Append(Date(r.Batch.ExpiryDate.Value));
            sb.Append(r.IsExpired
                ? "   (منتهية)"
                : "   (" + r.DaysUntilExpiry.ToString(En) + " يوم)");
            if (!string.IsNullOrWhiteSpace(r.Batch.BatchNumber))
                sb.Append("   [").Append(r.Batch.BatchNumber).Append(']');
            sb.Append('\n');
        }

        if (rows.Count > show)
            sb.Append("… و").Append((rows.Count - show).ToString(En)).Append(" دفعة أخرى.");

        return sb.ToString().TrimEnd();
    }

    // ---------------- prices ----------------

    /// <summary>One drug's price — the answer to typing a name or scanning a barcode.</summary>
    public static string Price(PriceLine p)
    {
        var sb = new StringBuilder();
        sb.Append("💊 ").Append(p.Item.DisplayName).Append('\n');
        if (!string.IsNullOrWhiteSpace(p.Item.GenericName) &&
            !string.Equals(p.Item.GenericName, p.Item.NameEn, StringComparison.OrdinalIgnoreCase))
            sb.Append(p.Item.GenericName).Append('\n');
        sb.Append('\n');

        if (p.Unpriced)
        {
            sb.Append("⚠️ لم يُحدَّد سعر لهذا الصنف بعد، ولا يظهر في نقطة البيع.");
            return sb.ToString();
        }

        // Through UnitConverter — the same path the till quotes from, so these are the prices a
        // customer at the counter is charged.
        if (p.Item.StripsPerBox > 1) sb.Append("سعر العلبة: ").Append(Money(p.BoxPrice)).Append('\n');
        if (p.Item.UnitsPerStrip > 1) sb.Append("سعر الشريط: ").Append(Money(p.StripPrice)).Append('\n');
        sb.Append("سعر الحبة: ").Append(Money(p.UnitPrice)).Append('\n');

        sb.Append('\n').Append("المتوفر: ").Append(Quantity(PackQuantity.Of(p.Item, p.AvailableUnits)));
        if (p.AvailableUnits <= 0) sb.Append("  ⚠️ نفد");

        return sb.ToString();
    }

    /// <summary>Several drugs matched a name — the owner picks by typing more.</summary>
    public static string PriceMatches(string term, IReadOnlyList<PriceLine> matches, int show)
    {
        var sb = new StringBuilder("🔎 نتائج \"");
        sb.Append(term).Append("\" — ").Append(matches.Count.ToString(En)).Append('\n').Append('\n');

        foreach (PriceLine p in matches.Take(show))
        {
            sb.Append("• ").Append(p.Item.DisplayName).Append(" — ");
            sb.Append(p.Unpriced ? "بدون سعر" : Money(p.BoxPrice > 0 ? p.BoxPrice : p.UnitPrice));
            if (p.AvailableUnits <= 0) sb.Append("  (نفد)");
            sb.Append('\n');
        }

        if (matches.Count > show)
            sb.Append("… و").Append((matches.Count - show).ToString(En)).Append(" غيرها.\n");

        sb.Append("\nاكتب اسماً أدق للحصول على التفاصيل.");
        return sb.ToString();
    }

    public static string PriceNotFound(string term)
        => "🔎 لا يوجد صنف باسم أو باركود: \"" + term + "\"\n\nجرّب جزءاً من الاسم، أو اكتب * لقائمة الأسعار.";

    private static string Quantity(PackQuantity q)
    {
        if (q.TotalUnits <= 0) return "صفر";

        var parts = new List<string>();
        if (q.Boxes > 0) parts.Add(q.Boxes.ToString(En) + " علبة");
        if (q.Strips > 0) parts.Add(q.Strips.ToString(En) + " شريط");
        if (q.Units > 0) parts.Add(q.Units.ToString(En) + " حبة");
        return parts.Count > 0 ? string.Join(" + ", parts) : q.TotalUnits.ToString(En) + " حبة";
    }
}
