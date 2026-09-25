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
    }
}
