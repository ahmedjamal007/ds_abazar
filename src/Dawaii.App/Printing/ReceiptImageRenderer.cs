using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dawaii.App.Printing
{
    /// <summary>Result of rendering a receipt: the bitmap plus a PNG accessor for preview/debug.</summary>
    public sealed class RenderedReceipt
    {
        public RenderedReceipt(RenderTargetBitmap bitmap)
        {
            Bitmap = bitmap;
        }

        public RenderTargetBitmap Bitmap { get; }
        public int Width => Bitmap.PixelWidth;
        public int Height => Bitmap.PixelHeight;

        public byte[] ToPng()
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Lays out a ReceiptDocument with WPF FormattedText (DirectWrite shapes and
    /// connects Arabic correctly) and rasterizes it to a white bitmap sized to the
    /// printer's dot width. Must be called on an STA thread (the UI thread in the app).
    /// </summary>
    public static class ReceiptImageRenderer
    {
        // 80 mm heads print 576 dots; 58 mm heads print 384.
        public const int Width80mm = 576;
        public const int Width58mm = 384;

        public static RenderedReceipt Render(ReceiptDocument document, int width = Width80mm, double baseFontSize = 20)
        {
            var borderPen = new Pen(Brushes.Black, 1.4);
            borderPen.Freeze();

            var ctx = new RenderContext
            {
                Family = new FontFamily("Segoe UI, Tahoma, Arial"),
                // Invariant keeps Latin digits (no Arabic-Indic substitution) while
                // DirectWrite still shapes the Arabic script by Unicode.
                Culture = CultureInfo.InvariantCulture,
                BaseSize = baseFontSize,
                PixelsPerDip = 1.0,
                Foreground = Brushes.Black,
                BorderPen = borderPen,
                Gap = 5,
            };

            const double sidePad = 12;
            const double topPad = 14;
            double contentWidth = width - 2 * sidePad;

            double contentHeight = Layout.MeasureStack(document.Elements, ctx, contentWidth, ctx.Gap);
            int height = (int)System.Math.Ceiling(contentHeight + 2 * topPad);
            if (height < 1) height = 1;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                Layout.DrawStack(dc, document.Elements, sidePad, topPad, contentWidth, ctx, ctx.Gap);
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return new RenderedReceipt(rtb);
        }
    }
}
