using System;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Ties the pipeline together: render a ReceiptDocument to a bitmap, convert
    /// to an ESC/POS raster, and send it raw to the spooler. Render calls use WPF,
    /// so Print/RenderPng must run on an STA thread (the WPF UI thread in the app).
    /// </summary>
    public sealed class PrintService
    {
        private readonly int _width;
        private readonly EscPosOptions _options;

        public PrintService(int width = ReceiptImageRenderer.Width80mm, EscPosOptions options = null)
        {
            _width = width;
            _options = options ?? new EscPosOptions();
        }

        /// <summary>Renders + sends a receipt to the named printer. Throws on failure.</summary>
        public void Print(string printerName, ReceiptDocument document)
        {
            if (string.IsNullOrWhiteSpace(printerName))
            {
                throw new InvalidOperationException("لم يتم تحديد طابعة الإيصالات");
            }
            var bytes = BuildBytes(document);
            RawPrinterHelper.SendBytesToPrinter(printerName, bytes);
        }

        /// <summary>Builds the ESC/POS byte stream without sending (for tests/preview).</summary>
        public byte[] BuildBytes(ReceiptDocument document)
        {
            var rendered = ReceiptImageRenderer.Render(document, _width);
            var mono = EscPosRaster.ToMono(rendered.Bitmap, _options.Threshold, _options.Dither);
            return EscPosRaster.BuildDocument(mono, _options);
        }

        /// <summary>Renders a receipt to PNG bytes (for the on-screen preview pane).</summary>
        public byte[] RenderPng(ReceiptDocument document)
        {
            return ReceiptImageRenderer.Render(document, _width).ToPng();
        }
    }
}
