namespace Dawaii.Core.Models
{
    /// <summary>Application roles (SRS 2.1 / FR-USR-02).</summary>
    public enum Role
    {
        /// <summary>The owner / manager: everything, including money, staff and settings.</summary>
        Admin,

        /// <summary>The person at the counter: sells, returns, logs their own expenses.</summary>
        Cashier,

        /// <summary>"موظف ذو امتيازات" (V1.8) — a cashier the manager trusts with the stockroom: the
        /// catalog, stock and barcodes are open to them, everything else still stays a cashier's.</summary>
        FullEmployee
    }

    /// <summary>The sellable unit tree: 1 Box = strips_per_box Strips, 1 Strip = units_per_strip Units.</summary>
    public enum UnitType
    {
        Unit,
        Strip,
        Box
    }

    public enum SaleType
    {
        Cash,
        Credit
    }

    public enum SaleStatus
    {
        Completed,

        /// <summary>Some items (or some of their quantity) came back; the rest of the invoice stands.</summary>
        PartiallyReturned,

        /// <summary>Every unit on the invoice has been returned.</summary>
        Returned
    }

    /// <summary>Charge increases a customer's debt (credit sale); Payment reduces it (repayment).</summary>
    public enum DebtTransactionType
    {
        Charge,
        Payment
    }

    public enum BackupStatus
    {
        Success,
        Failed
    }

    /// <summary>Where a supplier's invoice stands. Derived from the amounts on the invoice rather than
    /// stored, so it cannot claim something the totals disagree with.</summary>
    public enum PurchaseInvoiceStatus
    {
        Unpaid,
        PartiallyPaid,
        Paid
    }
}
