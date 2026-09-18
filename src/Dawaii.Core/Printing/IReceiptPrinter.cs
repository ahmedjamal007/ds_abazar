using Dawaii.Core.Models;

namespace Dawaii.Core.Printing
{
    /// <summary>
    /// Abstraction over receipt printing (FR-POS-07, DECISIONS D-09). The POS never depends on a
    /// printer being present: the default <see cref="NullReceiptPrinter"/> lets sales complete with none.
    /// </summary>
    public interface IReceiptPrinter
    {
        bool IsAvailable { get; }
        void Print(Sale sale, ReceiptInfo info);
    }

    /// <summary>Default no-op printer — used when no printer is configured.</summary>
    public class NullReceiptPrinter : IReceiptPrinter
    {
        public bool IsAvailable => false;
        public void Print(Sale sale, ReceiptInfo info) { /* intentionally does nothing */ }
    }
}
