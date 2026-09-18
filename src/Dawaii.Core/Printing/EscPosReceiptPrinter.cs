using System.Collections.Generic;
using System.Text;
using Dawaii.Core.Models;

namespace Dawaii.Core.Printing
{
    /// <summary>
    /// Prints to an ESC/POS thermal printer by name. Arabic bytes are encoded with Windows-1256,
    /// which many ESC/POS printers support as a code page; exact rendering is verified on real
    /// hardware (see MANUAL_TEST_CHECKLIST). Falls back gracefully — never blocks a sale.
    /// </summary>
    public class EscPosReceiptPrinter : IReceiptPrinter
    {
        private const byte ESC = 0x1B;
        private const byte GS = 0x1D;

        private readonly string _printerName;
        private readonly Encoding _encoding;

        public EscPosReceiptPrinter(string printerName)
        {
            _printerName = printerName;
            // Windows-1256 is the Arabic code page; registered via CodePagesEncodingProvider on .NET.
            _encoding = GetArabicEncoding();
        }

        public bool IsAvailable => !string.IsNullOrEmpty(_printerName);

        public void Print(Sale sale, ReceiptInfo info)
        {
            if (!IsAvailable) return;
            var bytes = new List<byte>();

            // Initialise, set Arabic code page (page 22 = CP1256 on many printers), right-justify.
            bytes.AddRange(new byte[] { ESC, 0x40 });           // ESC @  initialise
            bytes.AddRange(new byte[] { ESC, 0x74, 22 });       // ESC t 22  select CP1256
            bytes.AddRange(new byte[] { ESC, 0x61, 2 });        // ESC a 2  right align (RTL feel)

            foreach (string line in ReceiptContent.BuildLines(sale, info))
            {
                bytes.AddRange(_encoding.GetBytes(line));
                bytes.Add(0x0A);                                // line feed
            }

            bytes.AddRange(new byte[] { 0x0A, 0x0A, 0x0A });    // feed
            bytes.AddRange(new byte[] { GS, 0x56, 0x00 });      // GS V 0  full cut

            RawPrinterHelper.SendBytes(_printerName, bytes.ToArray());
        }

        private static Encoding GetArabicEncoding()
        {
            try { return Encoding.GetEncoding(1256); }
            catch { return Encoding.UTF8; }
        }
    }
}
