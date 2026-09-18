namespace Dawaii.Core.Printing
{
    /// <summary>Static pharmacy/terminal details printed on every receipt header/footer.</summary>
    public class ReceiptInfo
    {
        public string PharmacyName { get; set; } = "دوائي";
        public string Currency { get; set; } = "ج.س";
        public string CashierName { get; set; }
        public string Terminal { get; set; }
        public string Footer { get; set; } = "شكراً لزيارتكم";

        /// <summary>Characters per line for the target printer (58mm ≈ 32, 80mm ≈ 48).</summary>
        public int Width { get; set; } = 32;
    }
}
