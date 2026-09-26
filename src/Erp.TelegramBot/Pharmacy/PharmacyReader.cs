using Dawaii.Core;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Microsoft.Extensions.Logging;

namespace Erp.TelegramBot.Pharmacy;

/// <summary>
/// The bot's read-only window onto the pharmacy's database.
///
/// Built from the ERP's own services and repositories rather than fresh SQL, so the bot's idea of
/// "available" is the same as the till's: sellable batches only, disposed stock excluded, expiry
/// ordered the way FEFO allocates. A second implementation of "how much is on the shelf" that drifts
/// from the first would be worse than no bot at all — the manager would be reading a number nobody
/// else in the building agrees with.
///
/// Nothing here writes.
/// </summary>
public sealed class PharmacyReader : IPharmacyReader
{
    private readonly InventoryService _inventory;
    private readonly CodeService _codes;
    private readonly ReportService _reports;
    private readonly PosService _pos;
    private readonly EmployeeService _employees;
    private readonly DebtService _debts;
    private readonly SupplierService _suppliers;
    private readonly IUserRepository _users;
    private readonly IItemRepository _items;
    private readonly IStockRepository _stock;
    private readonly ILogger<PharmacyReader> _log;

    /// <summary>Most a name search will consider. Beyond a handful the manager should type more.</summary>
    public const int SearchLimit = 12;

    public PharmacyReader(
        InventoryService inventory,
        CodeService codes,
        ReportService reports,
        PosService pos,
        EmployeeService employees,
        DebtService debts,
        SupplierService suppliers,
        IUserRepository users,
        IItemRepository items,
        IStockRepository stock,
        ILogger<PharmacyReader> log)
    {
        _inventory = inventory;
        _codes = codes;
        _reports = reports;
        _pos = pos;
        _employees = employees;
        _debts = debts;
        _suppliers = suppliers;
        _users = users;
        _items = items;
        _stock = stock;
        _log = log;
    }

    public StockDetail? FindOne(string query)
    {
        string wanted = (query ?? "").Trim();
        if (wanted.Length == 0) return null;

        // Barcode first: a manager reading a code off a box means that drug, not the seven whose
        // names begin similarly.
        Item? item = _codes.ResolveItem(wanted);

        if (item == null)
        {
            IReadOnlyList<ItemStockView> matches = _inventory.Search(wanted, SearchLimit);
            if (matches.Count != 1) return null;        // none, or ambiguous — the caller asks
            item = matches[0].Item;
        }

        return Detail(item);
    }

    public IReadOnlyList<Item> FindMany(string query, int limit)
    {
        string wanted = (query ?? "").Trim();
        if (wanted.Length == 0) return [];

        return _inventory.Search(wanted, Math.Max(1, limit))
            .Select(v => v.Item)
            .ToList();
    }

    public IReadOnlyList<LowStockLine> LowStock()
    {
        return _inventory.GetLowStock()
            .Select(v => new LowStockLine(v.Item, v.AvailableUnits))
            // Proportionally worst first, then by name so the order is stable between pages. An item
            // at 2 of 100 has to outrank one at 95 of 100; a flat sort by units left would bury the
            // urgent one under bulkier drugs.
            .OrderBy(l => l.Coverage)
            .ThenBy(l => l.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- reports ----------------

    public DailyReport Sales(int erpUserId, Period period)
        => _reports.Range(User(erpUserId), period.FromInclusive, period.ToExclusive);

    public IReadOnlyList<Sale> Invoices(Period period)
        => _pos.SalesInRange(period.FromInclusive, period.ToExclusive);

    public IReadOnlyList<BestSellerRow> BestSellers(int erpUserId, Period period, int limit)
        => _reports.BestSellers(User(erpUserId), period.FromInclusive, period.ToExclusive, limit);

    public IReadOnlyList<DeadStockRow> DeadStock(int erpUserId, int days)
        => _reports.DeadStock(User(erpUserId), days);

    // ---------------- the owner's remote assistant ----------------

    public TakingsLine Takings(int erpUserId, Period period)
    {
        Dawaii.Core.Models.User actor = User(erpUserId);
        DailyReport totals = _reports.Range(actor, period.FromInclusive, period.ToExclusive);

        // The payment split comes from the pharmacy's OWN reconciliation — the same code behind the
        // shift screen — so Cash/Bankak/Fawry/OCash on a phone are the figures on the manager's
        // monitor. DailyReport only knows cash-versus-credit and would have been the wrong source.
        ShiftTill till = ShiftReconciliation.Build(
            _pos.SalesInRange(period.FromInclusive, period.ToExclusive),
            employees: null, purchases: 0m, supplierCash: 0m);

        return new TakingsLine(till, totals.CreditTotal, totals.TransactionCount,
            totals.ReturnedCount, totals.ReturnedTotal, totals.ProfitVisible, totals.TotalProfit);
    }

    public IReadOnlyList<EmployeeTakings> TakingsByEmployee(int erpUserId, Period period)
    {
        Dawaii.Core.Models.User actor = User(erpUserId);

        // RangeReport enforces its own permission and is the Admin module's own grouping, so the
        // employee list here is the employee list there.
        IReadOnlyList<EmployeeDayRow> staff =
            _employees.RangeReport(actor, period.FromInclusive, period.ToExclusive);

        List<Sale> sales = _pos.SalesInRange(period.FromInclusive, period.ToExclusive).ToList();
        var result = new List<EmployeeTakings>();

        foreach (EmployeeDayRow row in staff)
        {
            List<Sale> theirs = sales.Where(s => s.UserId == row.User.Id).ToList();
            if (theirs.Count == 0 && row.SalesCount == 0) continue;   // nothing to report

            ShiftTill till = ShiftReconciliation.Build(theirs, null, 0m, 0m);
            decimal credit = theirs
                .Where(s => s.SaleType == SaleType.Credit && s.Status != SaleStatus.Returned)
                .Sum(s => s.NetTotal);

            result.Add(new EmployeeTakings(row.User, new TakingsLine(
                till, credit, theirs.Count,
                theirs.Count(s => s.ReturnedTotal > 0 || s.Status == SaleStatus.Returned),
                theirs.Sum(s => s.RefundedTotal),
                ProfitVisible: false, Profit: 0m)));
        }

        return result.OrderByDescending(e => e.Takings.GrandTotal).ToList();
    }

    public IReadOnlyList<ShiftLine> Shifts(int erpUserId, DateTime day)
    {
        Dawaii.Core.Models.User actor = User(erpUserId);
        DateTime from = day.Date, to = day.Date.AddDays(1);

        IReadOnlyList<EmployeeDayRow> staff = _employees.RangeReport(actor, from, to);
        List<Sale> sales = _pos.SalesInRange(from, to).ToList();

        var result = new List<ShiftLine>();
        foreach (EmployeeDayRow row in staff)
        {
            List<Sale> theirs = sales.Where(s => s.UserId == row.User.Id).ToList();

            // Somebody who neither signed in nor sold anything did not work that day.
            if (row.FirstLoginAt == null && theirs.Count == 0) continue;

            result.Add(new ShiftLine(
                row.User,
                day.Date,
                row.FirstLoginAt,
                // NOT a logout — the schema has none. The last sale is the closest honest thing.
                theirs.Count > 0 ? theirs.Max(s => s.CreatedAt) : (DateTime?)null,
                ShiftReconciliation.Build(theirs, null, 0m, 0m),
                theirs.Count,
                row.MoneyExpenses));
        }

        return result.OrderBy(s => s.FirstLoginAt ?? DateTime.MaxValue).ToList();
    }

    public IReadOnlyList<Customer> CustomersInDebt()
        => _debts.WithDebt().OrderByDescending(c => c.Balance).ToList();

    public decimal CustomerDebtTotal() => _debts.TotalOutstanding();

    public IReadOnlyList<StatementRow> CustomerStatement(int customerId)
        => _debts.GetStatement(customerId);

    public IReadOnlyList<Customer> FindCustomers(string term) => _debts.Search(term ?? "");

    public IReadOnlyList<Supplier> SuppliersOwed()
        => _suppliers.WithOutstanding().OrderByDescending(s => s.Outstanding).ToList();

    public decimal SupplierDebtTotal() => _suppliers.TotalOutstanding();

    public IReadOnlyList<PurchaseLine> Purchases(int erpUserId, Period period)
        => _suppliers.OrdersInRange(User(erpUserId), period.FromInclusive, period.ToExclusive)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new PurchaseLine(i))
            .ToList();

    public IReadOnlyList<NearExpiryRow> Expiring(int? windowDays = null)
        => _inventory.GetNearExpiry(windowDays);

    public IReadOnlyList<PriceLine> Prices(string term, int limit)
    {
        string wanted = (term ?? "").Trim();
        if (wanted.Length == 0) return [];

        // Barcode first, same as /stock: a code read off a box means that drug.
        Item byCode = _codes.ResolveItem(wanted);
        if (byCode != null)
            return [new PriceLine(byCode, Available(byCode.Id))];

        return _inventory.Search(wanted, Math.Max(1, limit))
            .Select(v => new PriceLine(v.Item, v.AvailableUnits))
            .ToList();
    }

    public IReadOnlyList<PriceLine> AllPrices()
        // Search with an empty term is the catalogue; the ERP caps it, and unpriced drugs are kept
        // so the owner can see WHICH ones still need a price rather than silently missing them.
        => _inventory.Search("", int.MaxValue)
            .Select(v => new PriceLine(v.Item, v.AvailableUnits))
            .OrderBy(p => p.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private int Available(int itemId)
    {
        try { return _inventory.GetView(itemId)?.AvailableUnits ?? 0; }
        catch { return 0; }
    }

    /// <summary>
    /// The real pharmacy account behind a Telegram link, loaded fresh.
    ///
    /// Handed to the ERP's report services so THEY decide what this person may see — BestSellers and
    /// DeadStock refuse a non-administrator outright, and the sales figures include profit only for
    /// one. The bot does not re-implement any of that, which means it cannot get it subtly wrong.
    /// </summary>
    private Dawaii.Core.Models.User User(int erpUserId)
    {
        Dawaii.Core.Models.User user = _users.GetById(erpUserId);
        if (user == null)
            throw new PermissionDeniedException("حساب دوائي المرتبط غير موجود.");
        return user;
    }

    private StockDetail Detail(Item item)
    {
        var batches = new List<BatchLine>();
        try
        {
            // Sellable only — the same set the till can allocate from, so the bot's total agrees with
            // what a cashier would be able to sell.
            batches = _stock.GetSellableBatches(item.Id)
                .OrderBy(b => b.ExpiryDate ?? DateTime.MaxValue)   // soonest to expire first, as FEFO does
                .Select(b => new BatchLine(
                    b.QuantityUnits, b.UnitsPerStrip, b.StripsPerBox, b.ExpiryDate, b.BatchNumber))
                .ToList();
        }
        catch (Exception ex)
        {
            // The headline total still comes from the service below, so a batch query that fails
            // degrades the answer rather than replacing it with an error.
            _log.LogWarning(ex, "Could not read batches for item {ItemId}.", item.Id);
        }

        ItemStockView? view = null;
        try { view = _inventory.GetView(item.Id); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read stock for item {ItemId}.", item.Id); }

        int available = view?.AvailableUnits ?? batches.Sum(b => b.Units);
        return new StockDetail(view?.Item ?? item, available, batches);
    }
}
