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
}
