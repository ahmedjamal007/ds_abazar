using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Counting a shelf the way a pharmacist counts it (V2.6).
    ///
    /// Stock is stored in single tablets, because that is the only unit every sale reduces to. It is
    /// not how anybody thinks about a shelf: "1,240 tablets" is a number nobody can picture, and
    /// "12 boxes and 4 strips" is a thing you can walk over and look at. The Telegram bot reports the
    /// second, so the division has to be right — a manager checking stock from their phone is going
    /// to act on it.
    /// </summary>
    [TestFixture]
    public class PackQuantityTests
    {
        /// <summary>10 tablets a strip, 10 strips a box — so 100 a box.</summary>
        private static Item Panadol() => new Item
        {
            NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10, IsActive = true
        };

        [Test]
        public void AFullShelf_ReadsAsBoxes()
        {
            PackQuantity q = PackQuantity.Of(Panadol(), 1200);

            Assert.That(q.Boxes, Is.EqualTo(12));
            Assert.That(q.Strips, Is.Zero);
            Assert.That(q.Units, Is.Zero);
            Assert.That(q.TotalUnits, Is.EqualTo(1200));
        }

        [Test]
        public void ABrokenBox_ReadsAsBoxesAndStrips()
        {
            PackQuantity q = PackQuantity.Of(Panadol(), 1240);

            Assert.That(q.Boxes, Is.EqualTo(12));
            Assert.That(q.Strips, Is.EqualTo(4));
            Assert.That(q.Units, Is.Zero);
        }

        [Test]
        public void ABrokenStrip_ShowsTheLooseTablets()
        {
            PackQuantity q = PackQuantity.Of(Panadol(), 1247);

            Assert.That(q.Boxes, Is.EqualTo(12));
            Assert.That(q.Strips, Is.EqualTo(4));
            Assert.That(q.Units, Is.EqualTo(7), "somebody bought seven tablets out of a strip");
            Assert.That(q.TotalUnits, Is.EqualTo(1247), "and the real total is still exact");
        }

        [Test]
        public void LessThanAStrip_IsJustTablets()
        {
            PackQuantity q = PackQuantity.Of(Panadol(), 6);

            Assert.That(q.Boxes, Is.Zero);
            Assert.That(q.Strips, Is.Zero);
            Assert.That(q.Units, Is.EqualTo(6));
        }

        [Test]
        public void TheStripTotal_IgnoresTheLooseRemainder()
        {
            // "124 strips" is the figure a manager wants when deciding whether to reorder; the stray
            // seven tablets are not a strip they can sell.
            Assert.That(PackQuantity.Of(Panadol(), 1247).TotalStrips, Is.EqualTo(124));
        }

        [Test]
        public void AnEmptyShelf_IsEmpty()
        {
            PackQuantity q = PackQuantity.Of(Panadol(), 0);

            Assert.That(q.IsEmpty, Is.True);
            Assert.That(q.Boxes, Is.Zero);
            Assert.That(q.Strips, Is.Zero);
            Assert.That(q.Units, Is.Zero);
        }

        // ---------------- packaging that is not the tidy case ----------------

        [Test]
        public void ADrugSoldLoose_DoesNotDivideByZero()
        {
            // A catalogue entry with zero strips per box is not hypothetical: a drug sold loose, or a
            // half-finished entry. Dividing by it would throw inside a bot reply, far from the cause.
            var loose = new Item { NameEn = "syrup", UnitsPerStrip = 0, StripsPerBox = 0, IsActive = true };

            PackQuantity q = PackQuantity.Of(loose, 17);

            Assert.That(q.TotalUnits, Is.EqualTo(17));
            Assert.That(q.Units, Is.EqualTo(17), "with no packaging, everything is a loose unit");
            Assert.That(q.Boxes, Is.Zero);
        }

        [Test]
        public void WhenStripsAreKnownButBoxesAreNot_OnlyStripsAreReported()
        {
            // Reporting boxes here would make every strip look like a box — a tenfold overstatement
            // to a manager deciding whether to reorder.
            PackQuantity q = PackQuantity.Of(unitsPerStrip: 10, stripsPerBox: 0, totalUnits: 47);

            Assert.That(q.Boxes, Is.Zero);
            Assert.That(q.Strips, Is.EqualTo(4));
            Assert.That(q.Units, Is.EqualTo(7));
            Assert.That(q.TotalUnits, Is.EqualTo(47));
        }

        [Test]
        public void ASingleUnitBox_IsCountedAsBoxes()
        {
            // An injection or a bottle: one unit per strip, one strip per box. 17 of them are 17
            // boxes, not 17 loose tablets.
            var vial = new Item { NameEn = "vial", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true };

            PackQuantity q = PackQuantity.Of(vial, 17);

            Assert.That(q.Boxes, Is.EqualTo(17));
            Assert.That(q.Strips, Is.Zero);
            Assert.That(q.Units, Is.Zero);
        }

        [Test]
        public void ABatchsOwnPackaging_CanDifferFromTheCatalogues()
        {
            // Older shipments of the same drug came in different boxes, and stock_batches records the
            // packaging per shipment for exactly that reason.
            PackQuantity q = PackQuantity.Of(unitsPerStrip: 12, stripsPerBox: 5, totalUnits: 127);

            Assert.That(q.Boxes, Is.EqualTo(2), "60 to a box");
            Assert.That(q.Strips, Is.EqualTo(0));
            Assert.That(q.Units, Is.EqualTo(7));
        }

        [Test]
        public void NegativeStock_IsReportedRatherThanWrappedAround()
        {
            // It should not exist. If a corrupt row produces it, the answer must be readable instead
            // of a huge positive number from integer wrap-around.
            PackQuantity q = PackQuantity.Of(Panadol(), -5);

            Assert.That(q.TotalUnits, Is.EqualTo(-5));
            Assert.That(q.Boxes, Is.Zero);
            Assert.That(q.IsEmpty, Is.True);
        }

        [Test]
        public void ANullItem_DoesNotThrow()
        {
            Assert.That(PackQuantity.Of(null, 42).TotalUnits, Is.EqualTo(42));
        }

        [Test]
        public void TheSplitAlwaysAddsBackUp()
        {
            // The property that matters: however it is divided, nothing is invented or lost.
            Item item = Panadol();
            for (int units = 0; units < 500; units++)
            {
                PackQuantity q = PackQuantity.Of(item, units);
                Assert.That(q.Boxes * 100 + q.Strips * 10 + q.Units, Is.EqualTo(units), $"at {units}");
            }
        }
    }
}
