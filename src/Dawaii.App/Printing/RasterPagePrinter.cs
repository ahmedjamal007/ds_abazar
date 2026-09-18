using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Prints an already-rendered slip as a picture, through the Windows driver (V2.3).
    ///
    /// The fallback for when the thermal path cannot be used — a PDF or XPS printer, or a head that
    /// refused. The slip has already been laid out and shaped at the head's width by the same renderer
    /// the thermal path uses, so what the driver receives is exactly what the roll would have shown;
    /// it is simply handed over as an image rather than as ESC/POS. Drawn at the page's printable
    /// width, so a narrow roll driver fills the roll and an A4 driver gets a slip-width column on the
    /// page instead of a receipt stretched to the margins.
    /// </summary>
    public static class RasterPagePrinter
    {
        /// <summary>The slip's own width in inches — the 80mm head's 576 dots at 203 dpi.</summary>
        private const float SlipWidthInches = 576f / 203f;

        public static void Print(IWin32Window owner, byte[] png, string printerName, string documentName)
        {
            if (png == null) throw new ArgumentNullException(nameof(png));

            using (var ms = new MemoryStream(png))
            using (var image = Image.FromStream(ms))
            using (var doc = new PrintDocument())
            {
                doc.DocumentName = documentName ?? "Dawaii";
                if (!string.IsNullOrWhiteSpace(printerName)) doc.PrinterSettings.PrinterName = printerName;
                doc.DefaultPageSettings.Margins = new Margins(20, 20, 20, 20);

                // A slip can be taller than one page on a page printer; walk it down in page-height bands.
                int drawnDots = 0;
                doc.PrintPage += (s, e) =>
                {
                    RectangleF area = e.MarginBounds;

                    // Width on paper: the slip's real width, or the printable width if that is narrower
                    // (a roll driver). Height scales with it so nothing is squashed.
                    float widthHundredths = Math.Min(area.Width, SlipWidthInches * 100f);
                    float scale = widthHundredths / image.Width;
                    int dotsPerPage = (int)(area.Height / scale);
                    int band = Math.Min(dotsPerPage, image.Height - drawnDots);

                    var src = new Rectangle(0, drawnDots, image.Width, band);
                    var dst = new RectangleF(area.Left, area.Top, widthHundredths, band * scale);
                    e.Graphics.DrawImage(image, dst, src, GraphicsUnit.Pixel);

                    drawnDots += band;
                    e.HasMorePages = drawnDots < image.Height;
                };

                doc.Print();
            }
        }
    }
}
