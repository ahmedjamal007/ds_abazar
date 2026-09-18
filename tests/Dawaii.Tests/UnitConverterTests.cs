using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class UnitConverterTests
    {
        // 1 box = 10 strips = 100 tablets (SRS FR-POS-04 example).
        private static Item Panadol() => new Item
        {
            Id = 1, NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m
        };

        [Test]
        public void UnitsPerBox_Is_StripsTimesUnits()
        {
            Assert.That(Panadol().UnitsPerBox, Is.EqualTo(100));
        }

        [TestCase(UnitType.Unit, 1, 1)]
        [TestCase(UnitType.Strip, 1, 10)]
        [TestCase(UnitType.Box, 1, 100)]
        [TestCase(UnitType.Box, 3, 300)]
        [TestCase(UnitType.Strip, 4, 40)]
        public void ToUnits_ConvertsCorrectly(UnitType type, int qty, int expectedUnits)
        {
            Assert.That(UnitConverter.ToUnits(Panadol(), qty, type), Is.EqualTo(expectedUnits));
        }

        [TestCase(UnitType.Unit, 2.0)]
        [TestCase(UnitType.Strip, 20.0)]
        [TestCase(UnitType.Box, 200.0)]
        public void PriceOf_ScalesByUnitsInType(UnitType type, double expected)
        {
            Assert.That(UnitConverter.PriceOf(Panadol(), type), Is.EqualTo((decimal)expected));
        }

        [Test]
        public void LineTotal_MultipliesPriceByQuantity()
        {
            // 2 boxes at 200 each = 400
            Assert.That(UnitConverter.LineTotal(Panadol(), 2, UnitType.Box), Is.EqualTo(400m));
            // 3 strips at 20 each = 60
            Assert.That(UnitConverter.LineTotal(Panadol(), 3, UnitType.Strip), Is.EqualTo(60m));
        }

        [Test]
        public void SingleUnitItem_BehavesAsUnits()
        {
            var item = new Item { UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 5m };
            Assert.That(UnitConverter.ToUnits(item, 7, UnitType.Box), Is.EqualTo(7));
            Assert.That(UnitConverter.LineTotal(item, 7, UnitType.Unit), Is.EqualTo(35m));
        }
    }
}
