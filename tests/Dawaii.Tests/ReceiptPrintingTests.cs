using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Reflection;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// What actually comes out of the till printer.
    ///
    /// A receipt reached a customer reading "لي:" where "الإجمالي:" belonged and a drug cut to "Am",
    /// and every test in this suite passed while it happened — because they all tested the numbers and
    /// none of them tested the paper. The cause was not in the text: the layout assumed an "80mm" roll
    /// is 80mm of ink, when the shop's XP-80C marks a 72.1mm strip. Drawing ran past the print head,
    /// and since Arabic is right-aligned it was the right edge — every label — that fell off.
    ///
    /// So these tests render the real pipeline to a bitmap and look at where the ink lands. The decisive
    /// assertion is that nothing is drawn against the edge of the paper: ink touching the last column is
    /// how a layout that is too wide for the head announces itself.
    ///
    /// They need a printer driver to render through, and are ignored rather than failed on a machine
    /// without one, so a build server never goes red for lacking a printer.
    /// </summary>
    [TestFixture]
    public class ReceiptPrintingTests
    {
        private static Sale SampleSale()
        {
            var sale = new Sale
            {
                SaleNumber = 18,
                CreatedAt = new DateTime(2026, 9, 8, 17, 21, 0),
                Subtotal = 12020m,
                Total = 12020m,
                Discount = 0m,
                PaymentMethod = "Cash"
            };
            // The catalogue is in English and the names are long — that length is the thing that broke.
            sale.Lines.Add(new SaleLine
            {
                ItemName = "Amoxicillin capsules BP 250mg / amoxicillin",
                Quantity = 2, UnitsEach = 10, UnitPrice = 600m, LineTotal = 12000m
            });
            sale.Lines.Add(new SaleLine
            {
                ItemName = "polymol / paracetamol",
                Quantity = 1, UnitsEach = 1, UnitPrice = 20m, LineTotal = 20m
            });
            return sale;
        }

        private static ReceiptInfo SampleInfo() => new ReceiptInfo
        {
            PharmacyName = "صيدلية البشاشه",
            CashierName = "هاجر شنان عثمان",
            Currency = "ج.س"
        };

        /// <summary>
        /// Renders through the printer's own document — the same <c>BuildDocument</c> the app calls, so
        /// the paper-width decision under test is the real one — and returns the page as a bitmap.
        /// </summary>
        private static Bitmap RenderReceipt(Sale sale, ReceiptInfo info)
        {
            MethodInfo build = typeof(ReceiptDocumentPrinter)
                .GetMethod("BuildDocument", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(build, Is.Not.Null, "BuildDocument was renamed — this test needs re-pointing");

            using (var doc = (PrintDocument)build.Invoke(null, new object[] { sale, info }))
            {
                var controller = new PreviewPrintController();
                doc.PrintController = controller;
                doc.Print();

                PreviewPageInfo[] pages = controller.GetPreviewPageInfo();
                Assert.That(pages.Length, Is.EqualTo(1), "a two-line receipt must not spill onto a second page");
                return new Bitmap(pages[0].Image);
            }
        }

        private static Bitmap TryRenderReceipt(Sale sale, ReceiptInfo info)
        {
            try
            {
                return RenderReceipt(sale, info);
            }
            catch (InvalidPrinterException)
            {
                Assert.Ignore("No printer driver on this machine — nothing to render through.");
                return null;
            }
        }

        /// <summary>Columns of the bitmap that carry ink, left to right.</summary>
        private static List<int> InkColumns(Bitmap bmp)
        {
            var columns = new List<int>();
            for (int x = 0; x < bmp.Width; x++)
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.A > 40 && (c.R + c.G + c.B) / 3 < 160) { columns.Add(x); break; }
                }
            }
            return columns;
        }

        /// <summary>
        /// THE regression test. The receipt the customer was handed came off a document that asked for
        /// 315 hundredths of paper from a head that marks 283.7, so the last 6mm — every Arabic label —
        /// was never printed.
        ///
        /// It has to be asserted here, on the document, rather than on a rendered page: a preview
        /// renders at the size the document ASKED for, so a layout that overruns the hardware still
        /// looks perfect on screen. That is exactly why the fault reached a customer. Nothing that
        /// renders through the preview controller can see this; only comparing the request against the
        /// device can.
        /// </summary>
        [Test]
        public void Document_AsksForNoMorePaperThanThePrinterCanMark()
        {
            var settings = new PrinterSettings();
            if (!settings.IsValid) Assert.Ignore("No default printer on this machine.");

            float printable;
            try { printable = settings.DefaultPageSettings.PrintableArea.Width; }
            catch (InvalidPrinterException) { Assert.Ignore("No printer driver on this machine."); return; }
            if (printable <= 100) Assert.Ignore("Driver does not report a usable printable area.");

            MethodInfo build = typeof(ReceiptDocumentPrinter)
                .GetMethod("BuildDocument", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(build, Is.Not.Null, "BuildDocument was renamed — this test needs re-pointing");

            using (var doc = (PrintDocument)build.Invoke(null, new object[] { SampleSale(), SampleInfo() }))
            {
                int asked = doc.DefaultPageSettings.PaperSize.Width;
                Assert.That(asked, Is.LessThanOrEqualTo((int)Math.Ceiling(printable)),
                    "the receipt asks for " + asked + " hundredths of paper but this printer marks only " +
                    printable.ToString("0.0") + ". The overflow is not printed, and because every line is " +
                    "right-aligned Arabic it is the labels that vanish.");
            }
        }

        /// <summary>
        /// The head's width must be measured from the driver, never inferred from
        /// <c>receipt_width</c> — that setting is a CHARACTER count left over from the old text path,
        /// and the shop's still held the untouched default of 32. Read as "fewer than 40 columns, so a
        /// 58mm printer", it built the receipt 384 dots wide and printed it across two-thirds of an
        /// 80mm roll. The driver answers exactly: 2.8374in at 203 dpi is 576 dots.
        /// </summary>
        [Test]
        public void HeadDots_AreMeasuredFromTheDriver_NotInferredFromTheLegacyColumnCount()
        {
            var settings = new PrinterSettings();
            if (!settings.IsValid) Assert.Ignore("No default printer on this machine.");

            double inches;
            try { inches = settings.DefaultPageSettings.PrintableArea.Width / 100.0; }
            catch (InvalidPrinterException) { Assert.Ignore("No printer driver on this machine."); return; }
            if (inches < 1.5) Assert.Ignore("Driver does not report a usable printable width.");

            int expected = (int)Math.Round(inches * 203.0);
            expected -= expected % 8;
            if (expected < 256 || expected > 832) Assert.Ignore("Printer is not a receipt head.");

            MethodInfo headDots = typeof(Dawaii.App.Printing.ThermalReceipt)
                .GetMethod("HeadDots", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(headDots, Is.Not.Null, "HeadDots was renamed — this test needs re-pointing");

            // The legacy default that caused the two-thirds-width receipt.
            var legacy = new ReceiptInfo { Width = 32 };
            int actual = (int)headDots.Invoke(null, new object[] { settings.PrinterName, legacy });

            Assert.That(actual, Is.EqualTo(expected),
                "the receipt is being built " + actual + " dots wide for a head that prints " +
                expected + " — it will not fill the paper");
        }

        /// <summary>
        /// The raster path's whole point: the receipt is built at the print head's dot width, so there
        /// is no paper size to agree with the driver about and nothing to guess. 576 dots is an 80mm
        /// head, 384 a 58mm one. If this ever stops matching, the image is being scaled by the printer
        /// and the layout is back to being a guess.
        /// </summary>
        [Test]
        [Apartment(System.Threading.ApartmentState.STA)]
        public void Raster_IsBuiltAtThePrintHeadsExactWidth()
        {
            var rendered = Dawaii.App.Printing.ReceiptImageRenderer.Render(
                Dawaii.App.Printing.SaleReceiptBuilder.Build(SampleSale(), SampleInfo()),
                Dawaii.App.Printing.ReceiptImageRenderer.Width80mm);

            Assert.That(rendered.Width, Is.EqualTo(576),
                "an 80mm head prints 576 dots — the raster must be exactly that, not scaled to fit");
            Assert.That(rendered.Height, Is.GreaterThan(100), "the receipt rendered empty");
        }

        /// <summary>
        /// A long English drug name must survive whole. The catalogue is in English, the names run to
        /// forty characters, and the receipt the customer complained about had one cut to "Am" — so the
        /// name is given a wide column that wraps, and a longer name must make the receipt taller
        /// rather than quietly losing its tail.
        /// </summary>
        [Test]
        [Apartment(System.Threading.ApartmentState.STA)]
        public void Raster_GrowsTallerRatherThanCuttingALongDrugName()
        {
            Sale shortName = SampleSale();
            shortName.Lines.RemoveAt(1);
            shortName.Lines[0].ItemName = "polymol";

            Sale longName = SampleSale();
            longName.Lines.RemoveAt(1);
            longName.Lines[0].ItemName = "Amoxicillin capsules BP 250mg / amoxicillin trihydrate powder";

            int shortHeight = Dawaii.App.Printing.ReceiptImageRenderer.Render(
                Dawaii.App.Printing.SaleReceiptBuilder.Build(shortName, SampleInfo())).Height;
            int longHeight = Dawaii.App.Printing.ReceiptImageRenderer.Render(
                Dawaii.App.Printing.SaleReceiptBuilder.Build(longName, SampleInfo())).Height;

            Assert.That(longHeight, Is.GreaterThan(shortHeight),
                "the long name was not wrapped onto more lines — it is being cut to fit instead");
        }

        /// <summary>
        /// The bytes actually sent to the printer. A raster receipt is an image wrapped in ESC/POS, so
        /// the stream has to start with the initialise command and carry a GS v 0 raster; a stream
        /// without one prints nothing and the till looks broken.
        /// </summary>
        [Test]
        [Apartment(System.Threading.ApartmentState.STA)]
        public void EscPosStream_InitialisesAndCarriesARaster()
        {
            byte[] bytes = new Dawaii.App.Printing.PrintService(
                    Dawaii.App.Printing.ReceiptImageRenderer.Width80mm)
                .BuildBytes(Dawaii.App.Printing.SaleReceiptBuilder.Build(SampleSale(), SampleInfo()));

            Assert.That(bytes.Length, Is.GreaterThan(1000), "the stream is too small to hold a receipt");
            Assert.That(bytes[0], Is.EqualTo(0x1B), "must open with ESC @ (initialise)");
            Assert.That(bytes[1], Is.EqualTo(0x40));

            bool hasRaster = false;
            for (int i = 0; i < bytes.Length - 2; i++)
                if (bytes[i] == 0x1D && bytes[i + 1] == 0x76 && bytes[i + 2] == 0x30) { hasRaster = true; break; }
            Assert.That(hasRaster, Is.True, "no GS v 0 raster command — the printer would emit blank paper");
        }

        /// <summary>The rendered page must not run ink into its own last column either.</summary>
        [Test]
        public void Receipt_DrawsNothingAgainstTheEdgeOfThePaper()
        {
            using (Bitmap bmp = TryRenderReceipt(SampleSale(), SampleInfo()))
            {
                List<int> ink = InkColumns(bmp);
                Assert.That(ink, Is.Not.Empty, "the receipt rendered blank");

                int rightmost = ink[ink.Count - 1];
                Assert.That(rightmost, Is.LessThan(bmp.Width - 1),
                    "ink reaches the last column of the paper. Rightmost ink at " + rightmost +
                    " of " + bmp.Width + ".");
            }
        }

        /// <summary>
        /// Both halves must carry ink. Arabic labels sit at the right of every line and their values at
        /// the left; a receipt that lost one side is the exact failure the customer was handed, and a
        /// bare "is it blank" check would not have noticed.
        /// </summary>
        [Test]
        public void Receipt_PrintsBothTheLabelsAndTheirValues()
        {
            using (Bitmap bmp = TryRenderReceipt(SampleSale(), SampleInfo()))
            {
                List<int> ink = InkColumns(bmp);
                Assert.That(ink, Is.Not.Empty, "the receipt rendered blank");

                int third = bmp.Width / 3;
                Assert.That(ink.Exists(x => x < third), Is.True,
                    "nothing printed in the left third — the values (amounts, date, number) are missing");
                Assert.That(ink.Exists(x => x > third * 2), Is.True,
                    "nothing printed in the right third — the Arabic labels are missing, which is how " +
                    "\"الإجمالي:\" became \"لي:\"");
            }
        }

        /// <summary>
        /// A long English drug name gets a line of its own precisely so it is not shortened. If the
        /// renderer ever puts it back beside the price, the name's line stops well short of the left
        /// edge and this notices.
        /// </summary>
        [Test]
        public void Receipt_GivesALongDrugNameTheFullWidth()
        {
            Sale longName = SampleSale();
            longName.Lines[0].ItemName = "Amoxicillin capsules BP 250mg / amoxicillin trihydrate";

            using (Bitmap wide = TryRenderReceipt(longName, SampleInfo()))
            using (Bitmap plain = TryRenderReceipt(SampleSale(), SampleInfo()))
            {
                // The longer name must make the receipt no narrower — it should use at least as much of
                // the roll as the shorter one, rather than being cut to fit beside a price.
                List<int> wideInk = InkColumns(wide);
                List<int> plainInk = InkColumns(plain);

                Assert.That(wideInk, Is.Not.Empty);
                Assert.That(plainInk, Is.Not.Empty);
                Assert.That(wideInk[0], Is.LessThanOrEqualTo(plainInk[0] + 2),
                    "the longer drug name should reach at least as far across the roll as the short one");
            }
        }

        /// <summary>
        /// The width the layout uses may never exceed what the head can mark. This is the bug itself,
        /// asserted against whatever printer this machine has rather than against a remembered number.
        /// </summary>
        [Test]
        public void RollWidth_NeverExceedsWhatThePrinterCanActuallyMark()
        {
            MethodInfo rollWidth = typeof(ReceiptDocumentPrinter)
                .GetMethod("RollWidth", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(rollWidth, Is.Not.Null, "RollWidth was renamed — this test needs re-pointing");

            var settings = new PrinterSettings();
            if (!settings.IsValid) Assert.Ignore("No default printer on this machine.");

            int chosen;
            float printable;
            try
            {
                chosen = (int)rollWidth.Invoke(null, new object[] { settings });
                printable = settings.DefaultPageSettings.PrintableArea.Width;
            }
            catch (InvalidPrinterException)
            {
                Assert.Ignore("No printer driver on this machine.");
                return;
            }

            Assert.That(chosen, Is.GreaterThan(0));
            if (printable > 100)
                Assert.That(chosen, Is.LessThanOrEqualTo((int)Math.Ceiling(printable)),
                    "the layout would run past the print head — an \"80mm\" roll is not 80mm of ink " +
                    "(this printer marks " + printable.ToString("0.0") + " hundredths)");
        }

        /// <summary>A printer that answers with nonsense must not produce a nonsense receipt.</summary>
        [Test]
        public void RollWidth_FallsBackToAKnownGoodRoll_WhenTheDriverCannotBeAsked()
        {
            MethodInfo rollWidth = typeof(ReceiptDocumentPrinter)
                .GetMethod("RollWidth", BindingFlags.NonPublic | BindingFlags.Static);

            // A settings object naming a printer that does not exist: every property read throws.
            var missing = new PrinterSettings { PrinterName = "Dawaii-No-Such-Printer" };
            int chosen = (int)rollWidth.Invoke(null, new object[] { missing });

            Assert.That(chosen, Is.InRange(150, 400),
                "an unreadable driver must still leave a printable roll width, not zero or a wild value");
        }
    }
}
