using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Price increases (V2.3): a multiplier over the CURRENT SELLING PRICE — not the cost — rounded
    /// to a price a customer can be charged, previewed, then applied.
    ///
    /// The thing that has to hold is the relationship the rest of the app already relies on: the item
    /// stores one per-unit figure, and box = unit × units-per-box, strip = unit × units-per-strip,
    /// exactly. So the tests assert on all three prices for every plan, not just the one the user typed
    /// or saw — a rounding that made the strip clean but the box a non-multiple of it would pass a
    /// box-only test and still put two different prices on the same drug.
    /// </summary>
    [TestFixture]
    public class PricingTests
    {
        // ---------------- rounding ----------------

        [TestCase(5566.2, 5600)]
        [TestCase(5666.6, 5700)]
        [TestCase(4876.25, 4900)]
        [TestCase(5523, 5500)]
        [TestCase(47.5, 50)]
        [TestCase(432.9, 430)]
        [TestCase(1234.56, 1250)]
        [TestCase(55249, 55000)]
        [TestCase(55250, 55500)]
        [TestCase(5550, 5600)]        // a half rounds away from zero, as a shopkeeper does
        public void RoundToPractical_LandsOnAPriceSomeoneWouldActuallyQuote(double raw, double expected)
        {
            Assert.That(BatchPricing.RoundToPractical((decimal)raw), Is.EqualTo((decimal)expected));
        }

        [Test]
        public void RoundToPractical_NeverProducesFractions_AcrossTheWholeRange()
        {
            var rng = new Random(7);
            for (int i = 0; i < 2000; i++)
            {
                decimal raw = (decimal)(rng.NextDouble() * 100000);
                decimal rounded = BatchPricing.RoundToPractical(raw);
                Assert.That(rounded % 5m, Is.Zero, "raw " + raw + " -> " + rounded);
                Assert.That(Math.Abs(rounded - raw), Is.LessThanOrEqualTo(BatchPricing.StepFor(raw) / 2m + 0.0001m),
                    "rounding must be to the NEAREST step, not any step");
                if (raw >= 50m)
                    Assert.That(Math.Abs(rounded - raw) / raw, Is.LessThanOrEqualTo(0.026m),
                        "rounding is for a sayable price, not a repricing: " + raw + " -> " + rounded);
            }
        }

        [Test]
        public void RoundToPractical_HonoursAHouseStep()
        {
            Assert.That(BatchPricing.RoundToPractical(5523m, step: 1000m), Is.EqualTo(6000m));
            Assert.That(BatchPricing.RoundToPractical(5523m, step: 500m), Is.EqualTo(5500m));
            Assert.That(BatchPricing.RoundToPractical(5523m, step: 100m), Is.EqualTo(5500m));
        }

        [Test]
        public void RoundToPractical_ZeroOrNegative_IsZero()
        {
            Assert.That(BatchPricing.RoundToPractical(0m), Is.Zero);
            Assert.That(BatchPricing.RoundToPractical(-40m), Is.Zero);
        }

        // ---------------- the multiplier, and the box/strip/unit relationship ----------------

        [Test]
        public void Plan_MultipliesTheStripPrice_AndRebuildsTheBox()
        {
            // Sells at 50/unit, 10 units a strip, 10 strips a box: strip 500, box 5,000. Raise by 30%.
            PricePlanFigures f = BatchPricing.PlanFromSellingPrice(50m, stripsPerBox: 10, unitsPerStrip: 10, multiplier: 1.3m);

            Assert.That(f.StripPrice, Is.EqualTo(650m));
            Assert.That(f.BoxPrice, Is.EqualTo(6500m), "5,000 × 1.3");
            Assert.That(f.UnitPrice, Is.EqualTo(65m));
            Assert.That(f.WasRounded, Is.False, "650 was already clean");
        }

        [Test]
        public void Plan_RoundsTheStrip_SoTheBoxIsAnExactMultipleOfIt()
        {
            // A price that does not multiply nicely: 33.33/unit → strip 333.30 → ×1.3 = 433.29.
            PricePlanFigures f = BatchPricing.PlanFromSellingPrice(33.33m, 10, 10, 1.3m);

            Assert.That(f.StripPrice, Is.EqualTo(430m), "433.29 rounds to the nearest 10");
            Assert.That(f.BoxPrice, Is.EqualTo(4300m), "box = strip × 10, not a separately rounded figure");
            Assert.That(f.BoxPrice, Is.EqualTo(f.StripPrice * 10), "the relationship the POS relies on");
            Assert.That(f.WasRounded, Is.True);
        }

        [Test]
        public void Plan_UnitPrice_RebuildsTheBoxExactly()
        {
            // The item stores only the per-unit figure; the POS multiplies it back up. If that does not
            // reproduce the box price exactly, the receipt shows one price and the plan showed another.
            foreach (var (price, strips, units, mult) in new[]
            {
                (33.33m, 10, 10, 1.3m), (7m, 3, 12, 1.25m), (1234m, 1, 1, 1.5m), (0.5m, 4, 30, 2m)
            })
            {
                PricePlanFigures f = BatchPricing.PlanFromSellingPrice(price, strips, units, mult);
                // The per-unit figure can be a repeating decimal (330 ÷ 36); every consumer — the line
                // total, the receipt, the grid — rounds to 2dp, so that is the precision that has to hold.
                Assert.That(decimal.Round(f.UnitPrice * strips * units, 2), Is.EqualTo(f.BoxPrice), $"price {price} × {mult}");
                Assert.That(decimal.Round(f.UnitPrice * units, 2), Is.EqualTo(f.StripPrice));
            }
        }

        [Test]
        public void Plan_ABoxThatIsOneStrip_RoundsTheBoxItself()
        {
            PricePlanFigures f = BatchPricing.PlanFromSellingPrice(5000m, stripsPerBox: 1, unitsPerStrip: 1, multiplier: 1.3m);
            Assert.That(f.BoxPrice, Is.EqualTo(6500m));
            Assert.That(f.StripPrice, Is.EqualTo(6500m));
            Assert.That(f.UnitPrice, Is.EqualTo(6500m));
        }

        [Test]
        public void Plan_AllowsAMultiplierUnderOne_AsADiscount()
        {
            PricePlanFigures f = BatchPricing.PlanFromSellingPrice(5000m, 1, 1, 0.9m);
            Assert.That(f.BoxPrice, Is.EqualTo(4500m), "a price can be brought down as well as up");
        }

        [Test]
        public void Plan_RefusesAMultiplierThatIsNotPositive()
        {
            Assert.Throws<ValidationException>(() => BatchPricing.PlanFromSellingPrice(50m, 10, 10, 0m));
            Assert.Throws<ValidationException>(() => BatchPricing.PlanFromSellingPrice(50m, 10, 10, -1.3m));
        }

        [Test]
        public void Plan_RefusesAnItemWithNoPrice()
        {
            Assert.Throws<ValidationException>(() => BatchPricing.PlanFromSellingPrice(0m, 10, 10, 1.3m),
                "there is nothing to raise — the screen must flag it, not price it at zero");
        }

        [Test]
        public void RoundToPractical_NeverRoundsAPositivePriceToZero()
        {
            // A house step larger than the price: 78 with "round to 1000". Zero would be a free drug.
            Assert.That(BatchPricing.RoundToPractical(78m, step: 1000m), Is.EqualTo(1000m));
            Assert.That(BatchPricing.RoundToPractical(2m), Is.EqualTo(5m), "and under the magnitude rule too");
        }

        // ---------------- the service: preview ----------------

        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeSettingsRepository _settings;
        private FakeAuditRepository _audit;
        private PricingService _svc;
        private User _admin, _priv, _cashier;

        [SetUp]
        public void SetUp()
        {
            _stock = new FakeStockRepository();
            _items = new FakeItemRepository { Stock = _stock };
            _settings = new FakeSettingsRepository();
            _audit = new FakeAuditRepository();
            _svc = new PricingService(_items, _settings, _audit);
            _admin = new User { Id = 1, Username = "admin", Role = Role.Admin, IsActive = true };
            _priv = new User { Id = 2, Username = "priv", Role = Role.FullEmployee, IsActive = true };
            _cashier = new User { Id = 3, Username = "c", Role = Role.Cashier, IsActive = true };
        }

        private int Drug(string name, decimal costPerUnit, decimal? sellPerUnit, int strips = 10, int units = 10, bool manual = false)
            => _items.Add(new Item
            {
                NameEn = name, UnitsPerStrip = units, StripsPerBox = strips,
                PurchasePrice = costPerUnit, SellingPrice = sellPerUnit, ManualPrice = manual, IsActive = true
            });

        [Test]
        public void Preview_PlansEveryPricedItem_AndSaysWhyOthersAreSkipped()
        {
            int panadol = Drug("panadol", 50m, 60m);          // sells at 6,000 a box
            int nothing = Drug("mystery", 50m, null);         // a cost but no price yet
            int handSet = Drug("brufen", 40m, 70m, manual: true);

            var rows = _svc.Preview(_admin, new[] { panadol, nothing, handSet }, PriceOperation.Increase, 1.3m);

            Assert.That(rows.Select(r => r.Item.Id), Is.EqualTo(new[] { panadol, nothing, handSet }), "caller's order kept");
            Assert.That(rows[0].Status, Is.EqualTo(PricePlanStatus.Planned));
            Assert.That(rows[0].New.BoxPrice, Is.EqualTo(7800m), "6,000 × 1.3 — the selling price, not the 5,000 cost");
            Assert.That(rows[1].Status, Is.EqualTo(PricePlanStatus.NoPrice), "nothing to raise — flagged, not priced from cost");
            Assert.That(rows[1].New, Is.Null);
            Assert.That(rows[2].Status, Is.EqualTo(PricePlanStatus.ManualSkipped), "a hand-set price is protected by default");
        }

        // ---------------- no cost on file: the multiplier works from the shelf price ----------------

        [Test]
        public void Preview_MultipliesTheCurrentSellingPrice()
        {
            // B protin: on the shelf at 55,000 a box, no cost on file — irrelevant, the price is what counts.
            int bProtin = Drug("B protin", 0m, 55000m, strips: 1, units: 1);

            var row = _svc.Preview(_admin, new[] { bProtin }, PriceOperation.Increase, 1.25m).Single();

            Assert.That(row.Status, Is.EqualTo(PricePlanStatus.Planned));
            Assert.That(row.New.RawStripPrice, Is.EqualTo(68750m), "55,000 × 1.25");
            Assert.That(row.New.BoxPrice, Is.EqualTo(69000m), "68,750 rounded to the nearest 500 at that magnitude");
        }

        [Test]
        public void Preview_WithNoCost_AHouseStepKeepsTheRawFigureWhenItIsAlreadyClean()
        {
            _settings.Seed(PricingService.RoundingStepKey, "250");
            int bProtin = Drug("B protin", 0m, 55000m, strips: 1, units: 1);

            var row = _svc.Preview(_admin, new[] { bProtin }, PriceOperation.Increase, 1.25m).Single();
            Assert.That(row.New.BoxPrice, Is.EqualTo(68750m), "a 250 step leaves 68,750 as it is");
        }

        [Test]
        public void Preview_WithNoCost_RespectsPackaging()
        {
            // No cost, sells at 6,000 a box of 10 strips → the strip (600) is what gets multiplied.
            int item = Drug("syrup-strips", 0m, 60m, strips: 10, units: 10);

            var row = _svc.Preview(_admin, new[] { item }, PriceOperation.Increase, 1.3m).Single();
            Assert.That(row.New.StripPrice, Is.EqualTo(780m), "600 × 1.3");
            Assert.That(row.New.BoxPrice, Is.EqualTo(7800m), "box = strip × 10, as always");
        }

        [Test]
        public void Preview_WithNoCost_AllowsADiscountMultiplier()
        {
            // With no cost known the app cannot tell a loss from a discount, so it does not pretend to:
            // 0.9 on a shelf price is a discount the pharmacist chose.
            int bProtin = Drug("B protin", 0m, 55000m, strips: 1, units: 1);

            var row = _svc.Preview(_admin, new[] { bProtin }, PriceOperation.Decrease, 0.9m).Single();
            Assert.That(row.New.BoxPrice, Is.EqualTo(49500m));
        }

        [Test]
        public void Preview_IgnoresTheCost_EvenWhenItIsOnFile()
        {
            // Both on file: the SELLING price is what gets raised. A first cut multiplied the cost,
            // which would have taken a 9,000 drug DOWN to 6,500 — the owner corrected it.
            int panadol = Drug("panadol", 50m, 90m);      // sells at 9,000, costs 5,000
            var row = _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 1.3m).Single();
            Assert.That(row.New.BoxPrice, Is.EqualTo(11500m), "900 × 1.3 = 1,170 a strip → 1,150 → 11,500; not 5,000 × 1.3");
        }

        [Test]
        public void Preview_FlagsAPriceThatLandsBelowCost_ButDoesNotRefuseIt()
        {
            int loss = Drug("loss", 80m, 60m);            // costs 8,000 a box, sells at 6,000, and ×0.9 → 5,400
            var row = _svc.Preview(_admin, new[] { loss }, PriceOperation.Decrease, 0.9m).Single();
            Assert.That(row.Status, Is.EqualTo(PricePlanStatus.Planned), "a discount is the pharmacist's call");
            Assert.That(row.BelowCost, Is.True, "but the screen has to say so");

            int fine = Drug("fine", 50m, 60m);
            Assert.That(_svc.Preview(_admin, new[] { fine }, PriceOperation.Increase, 1.3m).Single().BelowCost, Is.False);
        }

        [Test]
        public void Preview_WithNoCost_AHandSetPriceIsStillProtected()
        {
            int bProtin = Drug("B protin", 0m, 55000m, strips: 1, units: 1, manual: true);
            Assert.That(_svc.Preview(_admin, new[] { bProtin }, PriceOperation.Increase, 1.25m).Single().Status,
                Is.EqualTo(PricePlanStatus.ManualSkipped));
        }

        [Test]
        public void Apply_ANoCostItemsNewPrice_ReachesTheTill()
        {
            int bProtin = Drug("B protin", 0m, 55000m, strips: 1, units: 1);
            var row = _svc.Preview(_admin, new[] { bProtin }, PriceOperation.Increase, 1.25m).Single();

            _svc.Apply(_admin, new[] { new PriceChange { ItemId = bProtin, SellingPerUnit = row.New.UnitPrice } });

            Assert.That(UnitConverter.PriceOf(_items.GetById(bProtin), UnitType.Box), Is.EqualTo(69000m));
        }

        [Test]
        public void Preview_IncludesHandSetPrices_OnlyWhenAsked()
        {
            int handSet = Drug("brufen", 40m, 70m, manual: true);

            var protectedRows = _svc.Preview(_admin, new[] { handSet }, PriceOperation.Increase, 1.3m, includeManual: false);
            var recalculated = _svc.Preview(_admin, new[] { handSet }, PriceOperation.Increase, 1.3m, includeManual: true);

            Assert.That(protectedRows.Single().Status, Is.EqualTo(PricePlanStatus.ManualSkipped));
            Assert.That(recalculated.Single().Status, Is.EqualTo(PricePlanStatus.Planned));
            Assert.That(recalculated.Single().New.BoxPrice, Is.EqualTo(9100m));   // 700 × 1.3 = 910 a strip, stays 910
        }

        [Test]
        public void Preview_SaysWhenTheItemIsAlreadyAtThatPrice()
        {
            // 6,500 a box = 650 a strip; ×1.001 is 650.65, which rounds back to 650 at that magnitude.
            // A multiplier of exactly 1.00 is refused before it gets here — see PriceOperationTests.
            int panadol = Drug("panadol", 50m, 65m);
            var rows = _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 1.001m);
            Assert.That(rows.Single().Status, Is.EqualTo(PricePlanStatus.Unchanged));
        }

        [Test]
        public void Preview_WritesNothing()
        {
            int panadol = Drug("panadol", 50m, 60m);
            _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 1.3m);
            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(60m), "a preview is a preview");
        }

        [Test]
        public void Preview_UsesTheHouseRoundingStep_WhenOneIsSet()
        {
            _settings.Seed(PricingService.RoundingStepKey, "1000");
            int panadol = Drug("panadol", 42.5m, 60m, strips: 1, units: 1);   // 60 × 1.3 = 78

            var rows = _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 1.3m);
            Assert.That(rows.Single().New.BoxPrice, Is.EqualTo(1000m), "the house step is one step at minimum, never zero");
        }

        [Test]
        public void Preview_RefusesABadMultiplier()
        {
            int panadol = Drug("panadol", 50m, 60m);
            Assert.Throws<ValidationException>(() => _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 0m));
            Assert.Throws<ValidationException>(() => _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, -2m));
            Assert.Throws<ValidationException>(() => _svc.Preview(_admin, new[] { panadol }, PriceOperation.Increase, 500m));
        }

        // ---------------- the service: who may ----------------

        [Test]
        public void ACashier_MayNotPreviewOrApply()
        {
            int panadol = Drug("panadol", 50m, 60m);
            Assert.Throws<PermissionDeniedException>(() => _svc.Preview(_cashier, new[] { panadol }, PriceOperation.Increase, 1.3m));
            Assert.Throws<PermissionDeniedException>(() => _svc.Apply(_cashier,
                new[] { new PriceChange { ItemId = panadol, SellingPerUnit = 65m } }));
            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(60m));
        }

        [Test]
        public void APrivilegedEmployee_May()
        {
            int panadol = Drug("panadol", 50m, 60m);
            Assert.DoesNotThrow(() => _svc.Preview(_priv, new[] { panadol }, PriceOperation.Increase, 1.3m));
            Assert.That(_svc.Apply(_priv, new[] { new PriceChange { ItemId = panadol, SellingPerUnit = 65m } }).Applied,
                Is.EqualTo(1), "the same people who set prices by entering a delivery");
        }

        // ---------------- the service: apply ----------------

        [Test]
        public void Apply_ChangesWhatThePosCharges()
        {
            int panadol = Drug("panadol", 50m, 60m);

            _svc.Apply(_admin, new[] { new PriceChange { ItemId = panadol, SellingPerUnit = 65m } });

            Item after = _items.GetById(panadol);
            Assert.That(UnitConverter.PriceOf(after, UnitType.Box), Is.EqualTo(6500m), "what the till quotes for a box");
            Assert.That(UnitConverter.PriceOf(after, UnitType.Strip), Is.EqualTo(650m));
            Assert.That(UnitConverter.PriceOf(after, UnitType.Unit), Is.EqualTo(65m));
        }

        [Test]
        public void Apply_BringsEveryBatchToTheNewPrice()
        {
            int panadol = Drug("panadol", 50m, 60m);
            int older = _stock.AddBatch(new StockBatch { ItemId = panadol, QuantityUnits = 100, StripsPerBox = 10, UnitsPerStrip = 10, BoxSellingPrice = 6000m });
            int repacked = _stock.AddBatch(new StockBatch { ItemId = panadol, QuantityUnits = 100, StripsPerBox = 4, UnitsPerStrip = 10, BoxSellingPrice = 2400m });

            _svc.Apply(_admin, new[] { new PriceChange { ItemId = panadol, SellingPerUnit = 65m } });

            Assert.That(_stock.GetBatch(older).BoxSellingPrice, Is.EqualTo(6500m));
            Assert.That(_stock.GetBatch(repacked).BoxSellingPrice, Is.EqualTo(2600m),
                "a batch packed 4 to a box sells the box at 4 × the same strip price");
        }

        [Test]
        public void Apply_RecordsWhetherThePriceWasTypedByHand()
        {
            int a = Drug("a", 50m, 60m);
            int b = Drug("b", 50m, 60m);

            _svc.Apply(_admin, new[]
            {
                new PriceChange { ItemId = a, SellingPerUnit = 65m, Operation = PriceOperation.Increase },
                new PriceChange { ItemId = b, SellingPerUnit = 70m, Operation = PriceOperation.Manual }
            });

            Assert.That(_items.GetById(a).ManualPrice, Is.False);
            Assert.That(_items.GetById(b).ManualPrice, Is.True);
        }

        [Test]
        public void Apply_ReportsAFailure_AndStillAppliesTheRest()
        {
            int panadol = Drug("panadol", 50m, 60m);

            PriceApplyResult r = _svc.Apply(_admin, new[]
            {
                new PriceChange { ItemId = 999999, SellingPerUnit = 65m },
                new PriceChange { ItemId = panadol, SellingPerUnit = 65m },
                new PriceChange { ItemId = panadol, SellingPerUnit = -1m }
            });

            Assert.That(r.Applied, Is.EqualTo(1));
            Assert.That(r.Failures.Count, Is.EqualTo(2), "a missing item and a negative price, both named");
            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(65m));
        }

        [Test]
        public void Apply_IsAudited()
        {
            int panadol = Drug("panadol", 50m, 60m);
            _svc.Apply(_admin, new[] { new PriceChange
            {
                ItemId = panadol, SellingPerUnit = 65m, Operation = PriceOperation.Increase, Multiplier = 1.3m
            }});
            Assert.That(_audit.Entries.Any(e => e.Action == "PriceIncrease" && e.EntityId == panadol),
                "the audit names which operation moved the price");
        }

        // ---------------- a price typed by hand ----------------

        [Test]
        public void FiguresForBoxPrice_DividesDown_WithoutRounding()
        {
            var item = new Item { StripsPerBox = 3, UnitsPerStrip = 10 };
            PricePlanFigures f = _svc.PreviewManual(item, 5000m).New;

            Assert.That(f.BoxPrice, Is.EqualTo(5000m), "exactly what was typed");
            Assert.That(f.StripPrice, Is.EqualTo(1666.67m));
            Assert.That(decimal.Round(f.UnitPrice * 30, 2), Is.EqualTo(5000m));
            Assert.That(f.WasRounded, Is.False);
        }

        [Test]
        public void FiguresForBoxPrice_RefusesANegativePrice()
        {
            Assert.Throws<ValidationException>(() => _svc.PreviewManual(new Item(), -5m));
        }

        // ---------------- against the real database ----------------

        [Test]
        public void OnSqlite_ANewPriceReachesTheTill_AndADeliveryLiftsTheManualFlag()
        {
            string path = Path.Combine(Path.GetTempPath(), "dawaii_price_" + Guid.NewGuid().ToString("N") + ".db");
            var db = new SqliteConnectionFactory(path);
            try
            {
                var init = new DatabaseInitializer(db);
                init.ApplySchemaAndSeed();
                init.EnsureDefaultAdmin();

                var items = new SqliteItemRepository(db);
                var stock = new SqliteStockRepository(db);
                var audit = new SqliteAuditRepository(db);
                User admin = new SqliteUserRepository(db).GetByUsername("admin");
                var pricing = new PricingService(items, new SqliteSettingsRepository(db), audit);
                var pos = new PosService(items, stock, new SqliteSaleStore(db), new SqliteCustomerRepository(db),
                    new SqliteSettingsRepository(db), audit);

                int panadol = items.Add(new Item { NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10, PurchasePrice = 50m, SellingPrice = 60m, IsActive = true });
                stock.AddBatch(new StockBatch { ItemId = panadol, QuantityUnits = 1000, StripsPerBox = 10, UnitsPerStrip = 10, BoxPurchasePrice = 5000m, BoxSellingPrice = 6000m, ExpiryDate = DateTime.Today.AddYears(1) });

                // A hand-set price, then a sale: the receipt must carry the new price.
                pricing.Apply(admin, new[] { new PriceChange { ItemId = panadol, SellingPerUnit = 70m, Operation = PriceOperation.Manual } });
                Assert.That(items.GetById(panadol).ManualPrice, Is.True);

                Sale sale = pos.Complete(admin, new List<CartLine> { new CartLine { ItemId = panadol, UnitType = UnitType.Strip, Quantity = 2 } },
                    SaleType.Cash, null, 0m, "t");
                Assert.That(sale.Total, Is.EqualTo(1400m), "2 strips at 700");
                Assert.That(stock.GetBatches(panadol).Single().BoxSellingPrice, Is.EqualTo(7000m), "the batch followed");

                // A delivery with its own price is a fresh deliberate entry: it sets the price and lifts the flag.
                var suppliers = new SupplierService(new SqliteSupplierRepository(db), items, audit);
                int co = suppliers.CreateSupplier(admin, "شركة");
                suppliers.RecordInvoice(admin, co, null, "INV-1", DateTime.Today, new[]
                {
                    new PurchaseInvoiceLine { ItemId = panadol, QuantityBoxes = 1, StripsPerBox = 10, BoxPurchasePrice = 5000m, BoxSellingPrice = 8000m }
                });
                Item after = items.GetById(panadol);
                Assert.That(UnitConverter.PriceOf(after, UnitType.Box), Is.EqualTo(8000m));
                Assert.That(after.ManualPrice, Is.False, "the invoice price is the newest deliberate entry");
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                GC.Collect(); GC.WaitForPendingFinalizers();
                foreach (string f in new[] { path, path + "-wal", path + "-shm" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }
}
