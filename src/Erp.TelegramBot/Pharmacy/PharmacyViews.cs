using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Erp.TelegramBot.Pharmacy;

/// <summary>
/// A shift, as far as this system can honestly describe one.
///
/// The ERP records LOGINS and nothing else — there is no logout, no shift open or close, anywhere in
/// the schema. So the start is real and the end is not: it is the time of this person's last sale
/// that day, which is the closest thing to "when they stopped working" that the data supports, and
/// it is labelled as a last transaction rather than dressed up as a logout.
///
/// The distinction matters. An owner deciding whether somebody left early should be told they are
/// looking at when the tills stopped, not at a clock-out that was never recorded.
/// </summary>
/// <param name="Employee">The account. Always named by full name where one is set.</param>
/// <param name="Day">The day this covers.</param>
/// <param name="FirstLoginAt">When they signed in. Recorded, and therefore real.</param>
/// <param name="LastSaleAt">Their last sale that day, or null if they made none. NOT a logout.</param>
/// <param name="Till">Takings split by payment channel, from the pharmacy's own reconciliation.</param>
/// <param name="SalesCount">Invoices they rang up.</param>
/// <param name="MoneyExpenses">Cash they drew from the till.</param>
public sealed record ShiftLine(
    User Employee,
    DateTime Day,
    DateTime? FirstLoginAt,
    DateTime? LastSaleAt,
    ShiftTill Till,
    int SalesCount,
    decimal MoneyExpenses)
{
    /// <summary>
    /// The employee's name as a person would say it. Full name where there is one, username only as
    /// a fallback — an owner reading a report wants "علاء", not "alaa01".
    /// </summary>
    public string Name => Named(Employee);

    internal static string Named(User user)
    {
        if (user == null) return "—";
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
    }
}

/// <summary>Takings for a period, split the way the money actually arrived.</summary>
/// <param name="Till">Cash, Bankak, Fawry, OCash — from ShiftReconciliation, the ERP's own split.</param>
/// <param name="CreditTotal">Sold on account. Not money in the drawer.</param>
/// <param name="InvoiceCount">How many sales.</param>
/// <param name="ReturnedCount">Invoices with anything returned.</param>
/// <param name="ReturnedTotal">Money refunded.</param>
/// <param name="ProfitVisible">Whether the ERP allowed this account to see profit.</param>
/// <param name="Profit">Profit, when visible.</param>
public sealed record TakingsLine(
    ShiftTill Till,
    decimal CreditTotal,
    int InvoiceCount,
    int ReturnedCount,
    decimal ReturnedTotal,
    bool ProfitVisible,
    decimal Profit)
{
    /// <summary>Everything sold, however it was paid for — including on account.</summary>
    public decimal GrandTotal =>
        Till.Cash + Till.Bankak + Till.Fawry + Till.Ocash + CreditTotal;
}

/// <summary>Takings for one employee, with their name.</summary>
/// <param name="Employee">The account.</param>
/// <param name="Takings">The same breakdown as the whole-pharmacy report.</param>
public sealed record EmployeeTakings(User Employee, TakingsLine Takings)
{
    public string Name => ShiftLine.Named(Employee);
}

/// <summary>A drug and what it currently sells for, for the price lookup.</summary>
/// <param name="Item">The catalogue entry.</param>
/// <param name="AvailableUnits">Sellable units, so a price is not quoted for something absent.</param>
public sealed record PriceLine(Item Item, int AvailableUnits)
{
    /// <summary>
    /// Per box, strip and single unit, through the SAME converter the till quotes from. Not a
    /// calculation of its own: a bot that priced a box differently from the counter would be worse
    /// than a bot with no prices.
    /// </summary>
    public decimal BoxPrice => UnitConverter.PriceOf(Item, UnitType.Box);
    public decimal StripPrice => UnitConverter.PriceOf(Item, UnitType.Strip);
    public decimal UnitPrice => UnitConverter.PriceOf(Item, UnitType.Unit);

    /// <summary>True when the drug has never been priced — the POS hides these.</summary>
    public bool Unpriced => !Item.SellingPrice.HasValue;
}

/// <summary>A purchase invoice, with who entered it.</summary>
/// <param name="Invoice">The invoice as the ERP holds it; UserName is already the full name.</param>
public sealed record PurchaseLine(PurchaseInvoice Invoice)
{
    public string EnteredBy => string.IsNullOrWhiteSpace(Invoice.UserName) ? "—" : Invoice.UserName;
}
