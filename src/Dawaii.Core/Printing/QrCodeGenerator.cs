using System;
using QRCoder;

namespace Dawaii.Core.Printing
{
    /// <summary>
    /// Generates QR codes as PNG bytes fully offline (FR-QRC-05). Uses QRCoder's PngByteQRCode,
    /// which is pure managed code with no System.Drawing or network dependency.
    /// </summary>
    public static class QrCodeGenerator
    {
        public static byte[] PngBytes(string text, int pixelsPerModule = 10)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("QR text is required.", nameof(text));
            using (var generator = new QRCodeGenerator())
            using (QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M))
            {
                var png = new PngByteQRCode(data);
                return png.GetGraphic(pixelsPerModule);
            }
        }

        /// <summary>The convention for an item's internal code when it has no manufacturer barcode.</summary>
        public static string InternalCodeFor(int itemId) => "DW-" + itemId;
    }
}
