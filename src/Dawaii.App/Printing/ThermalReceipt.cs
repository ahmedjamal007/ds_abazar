using System;
using System.Drawing.Printing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Prints the counter receipt by rasterising it and sending the image straight to the print
    /// spooler as ESC/POS (V2.2).
    ///
    /// The old path drew through the Windows driver and had to agree with it about the size of the
    /// paper. It never did: the app asked for an 80mm roll, the shop's XP-80C marks 72.1mm, and the
    /// difference took every Arabic label off the right-hand edge of the receipt. Rendering to the
    /// head's own dot width removes the negotiation — 576 dots is 576 dots — and rendering through
    /// DirectWrite rather than GDI shapes the Arabic correctly on the way.
    ///
    /// A virtual printer (PDF, XPS, OneNote) is the one case that must NOT get raw bytes: it would
    /// save a file full of ESC/POS control codes. Those fall back to the GDI printer, which is still
    /// the right tool for a driver that expects a drawn page.
    ///
    /// V2.3 splits the sale-specific entry points from the document ones underneath them, so the
    /// daily sales summary goes out through exactly the same head, width and fallback as a receipt.
    /// </summary>
    public static class ThermalReceipt
    {
        // ---------------- sales ----------------

        /// <summary>
        /// Prints a sale. Returns true if the raster path did it; false means the caller should use
        /// the GDI fallback, which is also what happens for a virtual printer.
        /// </summary>
        public static bool TryPrint(Sale sale, ReceiptInfo info, string configuredPrinter, out string error)
            => TryPrint(SaleReceiptBuilder.Build(sale, info), info, configuredPrinter, out error);

        /// <summary>Renders the receipt as a PNG for the on-screen preview, or null if it cannot.</summary>
        public static byte[] TryRenderPng(Sale sale, ReceiptInfo info, string configuredPrinter = null)
            => TryRenderPng(SaleReceiptBuilder.Build(sale, info), info, configuredPrinter);

        /// <summary>Shows the rendered receipt on screen, actual size, before any paper is used.</summary>
        public static void ShowPreview(IWin32Window owner, Sale sale, ReceiptInfo info)
            => ShowPreview(owner, SaleReceiptBuilder.Build(sale, info), info, "معاينة الإيصال");

        // ---------------- any document ----------------

        /// <summary>
        /// Prints a laid-out document on the receipt head. Returns false with no reason for a virtual
        /// printer (fall back quietly), and false WITH a reason when the head refused (tell the user).
        /// </summary>
        public static bool TryPrint(ReceiptDocument document, ReceiptInfo info, string configuredPrinter, out string error)
        {
            error = null;
            try
            {
                string printer = ResolvePrinter(configuredPrinter);
                if (string.IsNullOrWhiteSpace(printer)) { error = "لا توجد طابعة معرّفة."; return false; }

                // A PDF/XPS writer cannot be handed ESC/POS; let the caller draw a page instead.
                if (PrinterProfiles.IsVirtualPrinterName(printer)) return false;

                var service = new PrintService(HeadDots(printer, info), new EscPosOptions
                {
                    Cut = true,
                    PartialCut = true,
                    FeedLinesBeforeCut = 3,
                    // The raster already spans the head, so it needs no centring padding.
                    AlignCenter = false,
                    Threshold = 128,
                    Dither = false,
                });

                service.Print(printer, document);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Prints a document wherever the receipts go: the thermal head when there is one, otherwise a
        /// drawn page of the same rendering — so a PDF printer gets a picture of the slip, not a file
        /// of control codes. This is the one call the daily summary makes.
        /// </summary>
        public static void Print(IWin32Window owner, ReceiptDocument document, ReceiptInfo info, string documentName)
        {
            string configured = Session.Services != null ? Session.Services.ReceiptPrinterName : null;
            if (TryPrint(document, info, configured, out string reason)) return;

            if (!string.IsNullOrEmpty(reason))
            {
                Log.Error("Thermal " + documentName, new Exception(reason));
                Msg.Warn("تعذّرت الطباعة على طابعة الإيصالات:\n" + reason +
                         "\n\nسيتم محاولة الطباعة عبر تعريف الطابعة. تحقق من الطابعة.");
            }

            byte[] png = TryRenderPng(document, info, configured);
            if (png == null) throw new InvalidOperationException("تعذّر تجهيز الصفحة للطباعة.");
            RasterPagePrinter.Print(owner, png, configured, documentName);
        }

        /// <summary>Renders a document as a PNG at the head's width, or null if it cannot.</summary>
        public static byte[] TryRenderPng(ReceiptDocument document, ReceiptInfo info, string configuredPrinter = null)
        {
            try
            {
                var service = new PrintService(HeadDots(ResolvePrinter(configuredPrinter), info));
                return service.RenderPng(document);
            }
            catch { return null; }
        }

        /// <summary>Shows a document on screen, actual size, before any paper is used.</summary>
        public static void ShowPreview(IWin32Window owner, ReceiptDocument document, ReceiptInfo info, string title)
        {
            byte[] png = TryRenderPng(document, info, Session.Services != null ? Session.Services.ReceiptPrinterName : null);
            if (png == null) { Msg.Warn("تعذّرت المعاينة."); return; }

            using (var ms = new System.IO.MemoryStream(png))
            using (var image = System.Drawing.Image.FromStream(ms))
            using (var form = new BaseForm())
            {
                form.Text = title;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new System.Drawing.Size(
                    Math.Min(image.Width + 40, 700), Math.Min(image.Height + 90, 900));

                var box = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = System.Drawing.Color.White,
                    Image = new System.Drawing.Bitmap(image),
                };
                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, Height = 56,
                    FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10),
                };
                bar.Controls.Add(Theme.ActionButton("طباعة", () =>
                {
                    try { Print(form, document, info, title); }
                    catch (Exception ex) { Msg.Warn("تعذّرت الطباعة: " + ex.Message); }
                }, primary: true, width: 130));
                bar.Controls.Add(Theme.ActionButton("إغلاق", form.Close, width: 130));

                form.Controls.Add(box);
                form.Controls.Add(bar);
                form.ShowDialog(owner);
                box.Image.Dispose();
            }
        }

        // ---------------- the head ----------------

        /// <summary>Thermal receipt heads are 203 dpi almost without exception.</summary>
        private const double HeadDpi = 203.0;

        /// <summary>
        /// How many dots across the head actually prints — measured from the driver, not guessed.
        ///
        /// The first attempt guessed from the stored <c>receipt_width</c>, a CHARACTER count left over
        /// from the old text path. The shop's was 32, the default nobody had ever changed, so the
        /// receipt was built 384 dots wide and printed across two-thirds of an 80mm roll. The driver
        /// knows the answer exactly: this printer reports 2.8374in of printable width, which at 203 dpi
        /// is 576 dots — the full head.
        ///
        /// Snapped to a multiple of 8 because a raster row is packed into whole bytes, and clamped so a
        /// driver answering with nonsense cannot produce a receipt metres wide.
        /// </summary>
        private static int HeadDots(string printerName, ReceiptInfo info)
        {
            try
            {
                var settings = new PrinterSettings();
                if (!string.IsNullOrWhiteSpace(printerName)) settings.PrinterName = printerName;
                if (settings.IsValid)
                {
                    double inches = settings.DefaultPageSettings.PrintableArea.Width / 100.0;
                    int dots = (int)Math.Round(inches * HeadDpi);
                    dots -= dots % 8;
                    if (dots >= 256 && dots <= 832) return dots;
                }
            }
            catch { /* no driver, or one that will not answer — fall through to the setting */ }

            // Only if the printer cannot be asked: the old character-count setting, where a narrow
            // column count means a 58mm printer.
            return info != null && info.Width > 0 && info.Width < 40
                ? ReceiptImageRenderer.Width58mm
                : ReceiptImageRenderer.Width80mm;
        }

        /// <summary>The configured printer if the shop named one, otherwise Windows' default.</summary>
        private static string ResolvePrinter(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            try
            {
                var settings = new PrinterSettings();
                return settings.IsValid ? settings.PrinterName : null;
            }
            catch { return null; }
        }
    }
}
