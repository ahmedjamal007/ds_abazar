using System.Text;
using Dawaii.Core.Printing;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The code page Arabic receipts are encoded with (V2.5).
    ///
    /// This is the second thing the move off .NET Framework puts at risk, and it fails in the worst
    /// possible way: silently. The printer is sent ESC t 22 — "decode what follows as CP1256" — and
    /// then the text. On .NET Framework CP1256 is built into the runtime. On modern .NET it is not:
    /// it lives in System.Text.Encoding.CodePages and Encoding.GetEncoding(1256) throws until a
    /// provider is registered, at which point the code here quietly hands the printer UTF-8 instead.
    ///
    /// The head has already been told CP1256, so that does not print English. It prints a roll of
    /// garbage, with no exception, nothing in the log, and a customer holding it.
    ///
    /// This test passes today on net48 and is meant to KEEP passing after the retarget. If it ever
    /// fails, the provider registration is not working and receipts are broken.
    /// </summary>
    [TestFixture]
    public class ReceiptEncodingTests
    {
        [Test]
        public void ArabicReceipts_AreEncodedAsWindows1256_NotQuietlyAsUtf8()
        {
            Assert.That(EscPosReceiptPrinter.UsesArabicCodePage, Is.True,
                "the printer was already told ESC t 22; anything but CP1256 comes out as garbage");
        }

        [Test]
        public void TheCodePage_CanActuallyEncodeArabic()
        {
            Encoding cp1256 = Encoding.GetEncoding(1256);

            // "دواء" — four Arabic letters, one byte each in CP1256, two or three in UTF-8.
            byte[] bytes = cp1256.GetBytes("\u062f\u0648\u0627\u0621");

            Assert.That(bytes.Length, Is.EqualTo(4),
                "a single-byte code page is the whole reason the printer is told to use one");
            Assert.That(cp1256.GetString(bytes), Is.EqualTo("\u062f\u0648\u0627\u0621"),
                "and it survives the round trip");
        }

        // ---------------- the same root cause, in a second place (V2.5) ----------------

        /// <summary>
        /// CP1252, which nothing in this codebase asks for by name.
        ///
        /// PdfSharp encodes WinAnsi strings with it while saving ANY document — even one that is
        /// nothing but images, because the metadata dates go through it. On modern .NET the legacy
        /// code pages are not in the runtime, so the export died with NotSupportedException thrown
        /// from inside the library, several frames below anything this project wrote.
        ///
        /// It is the identical cause as the receipt bug above, surfacing somewhere completely
        /// unrelated, which is why registration belongs at startup rather than in whichever component
        /// happened to notice first.
        /// </summary>
        [Test]
        public void TheWinAnsiCodePage_IsAvailable_SoReportsCanBeSaved()
        {
            Assert.That(LegacyEncodings.IsAvailable(1252), Is.True,
                "without this, exporting any PDF throws from inside PdfSharp");
        }

        [Test]
        public void RegisteringTheCodePages_IsSafeToRepeat()
        {
            // Called from Program.Main, from the receipt printer and from the PDF exporter, because a
            // test run or any other host that never calls Main still has to work.
            Assert.DoesNotThrow(() =>
            {
                LegacyEncodings.Register();
                LegacyEncodings.Register();
                LegacyEncodings.Register();
            });

            Assert.That(LegacyEncodings.IsAvailable(1256), Is.True);
        }

        [Test]
        public void ACodePageThatDoesNotExist_IsReportedAsUnavailable_NotThrown()
        {
            Assert.That(LegacyEncodings.IsAvailable(999999), Is.False,
                "callers ask so they can fall back, not so they can catch");
        }

    }
}
