using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Erp.TelegramBot.Pharmacy;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// A pharmacy the tests control completely.
///
/// One shared fake rather than a copy per fixture: the reader's surface grows every phase, and three
/// near-identical stubs is three places to forget to update. It also records what it was asked, so a
/// test can assert that a command queried what it should — and, more usefully, that it did NOT query
/// what it should not.
///
/// <see cref="Refuse"/> stands in for the ERP turning a request down. That path matters: every report
/// goes through the pharmacy's own services, and BestSellers and DeadStock refuse a
/// non-administrator outright, so the bot has to pass that refusal on rather than swallow it.
/// </summary>
internal sealed class TestPharmacy : IPharmacyReader
{
    public StockDetail? One;
    public List<Item> Matches = [];
    public List<LowStockLine> Low = [];
    public DailyReport Totals = new();
    public List<Sale> InvoiceList = [];
    public List<BestSellerRow> Best = [];
    public List<DeadStockRow> Dead = [];

    /// <summary>When set, every report method throws it — as the ERP would for a demoted account.</summary>
    public DomainException? Refuse;

    public string? LastQuery;
    public int LowReads;
    public int ErpUserIdSeen;

    public StockDetail? FindOne(string query)
    {
        LastQuery = query;
        return One;
    }

    public IReadOnlyList<Item> FindMany(string query, int limit) => Matches;

    public IReadOnlyList<LowStockLine> LowStock()
    {
        LowReads++;
        return Low;
    }

    public DailyReport Sales(int erpUserId, Period period)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return Totals;
    }

    public IReadOnlyList<Sale> Invoices(Period period) => InvoiceList;

    public IReadOnlyList<BestSellerRow> BestSellers(int erpUserId, Period period, int limit)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return Best;
    }

    public IReadOnlyList<DeadStockRow> DeadStock(int erpUserId, int days)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return Dead;
    }

    // ---------------- the owner's reports ----------------

    public TakingsLine TakingsLine = new(new ShiftTill(), 0m, 0, 0, 0m, false, 0m);
    public List<EmployeeTakings> ByEmployee = [];
    public List<ShiftLine> ShiftRows = [];
    public List<Customer> Debtors = [];
    public decimal CustomerTotal;
    public List<StatementRow> Statement = [];
    public List<Customer> CustomerList = [];
    public List<Supplier> SupplierRows = [];
    public decimal SupplierTotal;
    public List<PurchaseLine> PurchaseRows = [];
    public List<NearExpiryRow> ExpiringRows = [];
    public List<PriceLine> PriceRows = [];
    public List<PriceLine> AllPriceRows = [];
    public string? LastPriceTerm;

    public TakingsLine Takings(int erpUserId, Period period)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return TakingsLine;
    }

    public IReadOnlyList<EmployeeTakings> TakingsByEmployee(int erpUserId, Period period)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return ByEmployee;
    }

    public IReadOnlyList<ShiftLine> Shifts(int erpUserId, DateTime day)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return ShiftRows;
    }

    public IReadOnlyList<Customer> CustomersInDebt() => Debtors;
    public decimal CustomerDebtTotal() => CustomerTotal;
    public IReadOnlyList<StatementRow> CustomerStatement(int customerId) => Statement;
    public IReadOnlyList<Customer> FindCustomers(string term) => CustomerList;

    public IReadOnlyList<Supplier> SuppliersOwed() => SupplierRows;
    public decimal SupplierDebtTotal() => SupplierTotal;

    public IReadOnlyList<PurchaseLine> Purchases(int erpUserId, Period period)
    {
        ErpUserIdSeen = erpUserId;
        if (Refuse != null) throw Refuse;
        return PurchaseRows;
    }

    public IReadOnlyList<NearExpiryRow> Expiring(int? windowDays = null) => ExpiringRows;

    public IReadOnlyList<PriceLine> Prices(string term, int limit)
    {
        LastPriceTerm = term;
        return PriceRows;
    }

    public IReadOnlyList<PriceLine> AllPrices() => AllPriceRows;
}
