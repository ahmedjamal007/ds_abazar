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
    private readonly IItemRepository _items;
    private readonly IStockRepository _stock;
    private readonly ILogger<PharmacyReader> _log;

    /// <summary>Most a name search will consider. Beyond a handful the manager should type more.</summary>
    public const int SearchLimit = 12;

    public PharmacyReader(
        InventoryService inventory,
        CodeService codes,
        IItemRepository items,
        IStockRepository stock,
        ILogger<PharmacyReader> log)
    {
        _inventory = inventory;
        _codes = codes;
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
