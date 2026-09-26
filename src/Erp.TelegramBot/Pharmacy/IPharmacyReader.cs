using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Erp.TelegramBot.Pharmacy;

/// <summary>One batch of a drug on the shelf, as the bot reports it.</summary>
/// <param name="Units">Single units remaining in this batch.</param>
/// <param name="UnitsPerStrip">This shipment's own packaging, which may differ from the catalogue's.</param>
/// <param name="StripsPerBox">As above.</param>
/// <param name="Expiry">Expiry date, or null when the batch has none recorded.</param>
/// <param name="BatchNumber">The supplier's lot number as printed, or null.</param>
public sealed record BatchLine(
    int Units, int UnitsPerStrip, int StripsPerBox, DateTime? Expiry, string? BatchNumber);

/// <summary>A drug with everything the bot needs to answer /stock about it.</summary>
/// <param name="Item">The catalogue entry.</param>
/// <param name="AvailableUnits">Sellable single units — excludes disposed batches.</param>
/// <param name="Batches">The batches making up that total, soonest to expire first.</param>
public sealed record StockDetail(Item Item, int AvailableUnits, IReadOnlyList<BatchLine> Batches);

/// <summary>
/// Everything the bot reads from the pharmacy's database, behind one seam.
///
/// READ-ONLY, and that is not a convention this interface merely hopes for — there is no method here
/// that writes, so the bot's whole access to pharmacy data is this surface. A background service
/// that could write to the till's own file is a background service that can corrupt it.
///
/// It exists as an interface so the report formatting — which is most of the code and all of the
/// wording — can be tested against fixed data, with no database and no pharmacy.
/// </summary>
public interface IPharmacyReader
{
    /// <summary>
    /// Finds one drug by barcode, then by name. Null when nothing matches.
    ///
    /// Barcode first because that is the unambiguous answer: a manager reading a code off a box wants
    /// that drug, not the seven whose names begin similarly.
    /// </summary>
    StockDetail? FindOne(string query);

    /// <summary>Drugs whose name matched more than one entry, so the manager can be asked which.</summary>
    IReadOnlyList<Item> FindMany(string query, int limit);

    /// <summary>
    /// Every drug at or below its reorder level, worst first.
    ///
    /// "Worst" is by how far below, proportionally — an item at 2 of 100 needs attention before one
    /// at 95 of 100, and a flat sort by remaining units would bury it under bulkier drugs.
    /// </summary>
    IReadOnlyList<LowStockLine> LowStock();

    // ---------------- reports (phase 5) ----------------
    //
    // Each of these takes the ERP user id of the manager who asked, and every one passes it to the
    // pharmacy's own service. That is not ceremony: ReportService.BestSellers and DeadStock REFUSE a
    // non-administrator, and Range decides whether profit is included from the same user. So the
    // bot's answers carry exactly the permissions of the real account behind the Telegram id, and a
    // manager demoted between linking and asking gets refused by the ERP rather than by the bot
    // remembering to check.

    /// <summary>Takings, invoice count, returns and — for an administrator — profit.</summary>
    DailyReport Sales(int erpUserId, Period period);

    /// <summary>The individual invoices behind those totals, for the exported file.</summary>
    IReadOnlyList<Sale> Invoices(Period period);

    /// <summary>What sold most. Administrator only, enforced by the ERP.</summary>
    IReadOnlyList<BestSellerRow> BestSellers(int erpUserId, Period period, int limit);

    /// <summary>Stock that has not moved. Administrator only, enforced by the ERP.</summary>
    IReadOnlyList<DeadStockRow> DeadStock(int erpUserId, int days);
}

/// <summary>A drug at or below its reorder level.</summary>
/// <param name="Item">The catalogue entry.</param>
/// <param name="AvailableUnits">Sellable single units remaining.</param>
public sealed record LowStockLine(Item Item, int AvailableUnits)
{
    /// <summary>The reorder level, in single units, as the pharmacy set it.</summary>
    public int ReorderLevel => Item.MinQuantity;

    /// <summary>
    /// How much of the reorder level is still covered, 0 to 1. Used for ordering, so that a drug
    /// almost gone outranks one merely at its threshold.
    /// </summary>
    public double Coverage => ReorderLevel <= 0
        ? (AvailableUnits <= 0 ? 0 : 1)
        : Math.Max(0, Math.Min(1, (double)AvailableUnits / ReorderLevel));

    /// <summary>Nothing left at all — the case worth putting first and marking.</summary>
    public bool IsOut => AvailableUnits <= 0;
}
