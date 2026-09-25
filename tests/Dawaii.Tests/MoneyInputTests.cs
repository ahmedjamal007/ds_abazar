using System.Globalization;
using System.Threading;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Reading an amount of money that somebody typed (V2.5) — under every culture, not just this
    /// developer's.
    ///
    /// This fixture exists because of the move off .NET Framework. Number formats come from Windows
    /// NLS today and from ICU on modern .NET, and the two disagree about Arabic: under ar-SD, ICU
    /// says the decimal mark is ٫ (U+066B). A bare culture-sensitive parse of "1250.50" therefore
    /// starts returning false on a machine where it worked for years — a customer's repayment
    /// refused, with nothing to explain why.
    ///
    /// Until now not one test in this suite set a culture. They all ran as en-US, the single culture
    /// where every one of these problems is invisible, so a green run after retargeting would have
    /// meant nothing at all. Each test here pins its own culture and puts it back afterwards.
    /// </summary>
    [TestFixture]
    public class MoneyInputTests
    {
        private CultureInfo _original;

        [SetUp]
        public void SetUp() => _original = Thread.CurrentThread.CurrentCulture;

        [TearDown]
        public void TearDown() => Thread.CurrentThread.CurrentCulture = _original;

        private static void Under(string culture)
            => Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);

        private static decimal Parsed(string text)
        {
            decimal value;
            Assert.That(MoneyInput.TryParse(text, out value), Is.True, "\"" + text + "\" should be a number");
            return value;
        }

        // ---------------- the plain form works everywhere ----------------

        [TestCase("en-US")]
        [TestCase("ar-SD")]   // the pharmacy's own
        [TestCase("ar-EG")]
        [TestCase("de-DE")]   // comma decimal, to prove the point is not about Arabic alone
        [TestCase("fr-FR")]
        public void APlainNumber_IsReadTheSameWayUnderAnyCulture(string culture)
        {
            Under(culture);

            Assert.That(Parsed("1250.50"), Is.EqualTo(1250.50m),
                "this is how every receipt and price list this program prints writes an amount");
            Assert.That(Parsed("0.25"), Is.EqualTo(0.25m));
            Assert.That(Parsed("7"), Is.EqualTo(7m));
        }

        [TestCase("en-US")]
        [TestCase("ar-SD")]
        [TestCase("de-DE")]
        public void AWholeSum_IsReadTheSameWayUnderAnyCulture(string culture)
        {
            Under(culture);
            Assert.That(Parsed("52000"), Is.EqualTo(52000m));
        }

        // ---------------- and so does what the machine's own culture says ----------------

        [Test]
        public void AGermanKeyboardsComma_MeansWhatGermanyMeansByIt()
        {
            Under("de-DE");
            Assert.That(Parsed("1250,50"), Is.EqualTo(1250.50m));
        }

        [Test]
        public void AnArabicKeyboardsDigits_AreNotADifferentNumber()
        {
            Under("ar-SD");

            // ١٢٥٠٫٥٠ — Arabic-Indic digits with the Arabic decimal mark, which is what the keyboard
            // on a Sudanese pharmacy PC actually produces.
            Assert.That(Parsed("١٢٥٠٫٥٠"), Is.EqualTo(1250.50m));
            Assert.That(Parsed("٥٠٠"), Is.EqualTo(500m));
        }

        [Test]
        public void AnAmountPastedWithADirectionMark_IsStillANumber()
        {
            Under("ar-SD");
            Assert.That(Parsed("‏1250.50"), Is.EqualTo(1250.50m),
                "copying a figure out of an Arabic document brings an invisible RTL mark with it");
        }

        [Test]
        public void ThousandsSeparators_AreAccepted_BecauseThisProgramPrintsThem()
        {
            Under("en-US");
            Assert.That(Parsed("52,000.50"), Is.EqualTo(52000.50m));
        }

        [Test]
        public void AGroupedGermanFigure_IsReadAsGermanyWritesIt()
        {
            Under("de-DE");
            Assert.That(Parsed("1.250,50"), Is.EqualTo(1250.50m));
        }

        /// <summary>
        /// The reason the parse order is what it is.
        ///
        /// German groups thousands with a dot, so a permissive culture-first parse reads "1250.50" as
        /// 125050 — a hundred times the amount — and reports success. The pharmacy's own stock forms
        /// did exactly that. A price silently multiplied by a hundred is the worst outcome available
        /// here: it is not refused, not flagged, and looks like a real figure.
        /// </summary>
        [TestCase("de-DE")]
        [TestCase("fr-FR")]
        [TestCase("it-IT")]
        [TestCase("es-ES")]
        public void ADotIsNeverReadAsThousands_HoweverTheMachineIsSet(string culture)
        {
            Under(culture);

            Assert.That(Parsed("1250.50"), Is.EqualTo(1250.50m));
            Assert.That(Parsed("1250.50"), Is.Not.EqualTo(125050m),
                "a hundredfold silent error on a price is worse than refusing the figure outright");
        }

        // ---------------- what is not a number ----------------

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        [TestCase("abc")]
        [TestCase("-")]
        [TestCase(".")]
        public void WhatIsNotANumber_IsRefused(string text)
        {
            Under("ar-SD");

            decimal value;
            Assert.That(MoneyInput.TryParse(text, out value), Is.False);
            Assert.That(value, Is.Zero, "a refused amount must not leave a stale figure behind");
        }

        [Test]
        public void ARefusedAmount_FallsBackToWhatTheCallerAsksFor()
        {
            Under("ar-SD");

            Assert.That(MoneyInput.Or("nonsense", 42m), Is.EqualTo(42m));
            Assert.That(MoneyInput.Or("1250.50", 42m), Is.EqualTo(1250.50m));
        }

        [Test]
        public void ANegativeAmount_IsStillANumber()
        {
            // Whether a screen ALLOWS a negative is the screen's business; reading one is not.
            Under("en-US");
            Assert.That(Parsed("-50.25"), Is.EqualTo(-50.25m));
        }
    }
}
