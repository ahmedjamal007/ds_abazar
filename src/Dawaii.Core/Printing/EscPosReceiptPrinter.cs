using System;
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

        /// <summary>
        /// Windows-1256, the Arabic code page the printer has just been told to expect.
        ///
        /// Modern .NET does not carry the legacy code pages; <see cref="LegacyEncodings"/> puts the
        /// provider in place. That matters here more than it looks, because the receipt has already
        /// been sent ESC t 22 telling the head to decode CP1256 — so falling back to UTF-8 does not
        /// print English, it prints a roll of garbage, with no exception and nothing logged.
        ///
        /// The fallback stays a last resort rather than a throw, because this runs while a sale is
        /// being rung up and a missing code page must not stop the pharmacy selling.
        /// </summary>
        private static Encoding GetArabicEncoding()
        {
            LegacyEncodings.Register();
            try { return Encoding.GetEncoding(1256); }
            catch { return Encoding.UTF8; }
        }

        /// <summary>
        /// True when receipts really are being encoded as Windows-1256, false when the code page was
        /// unreachable and UTF-8 is standing in — which the printer will render as garbage.
        /// </summary>
        public static bool UsesArabicCodePage => LegacyEncodings.IsAvailable(1256);

    }
}
