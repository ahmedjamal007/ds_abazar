using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Raising and lowering prices (V2.3.2).
    ///
    /// Both operations are the same arithmetic — <c>new = current × multiplier</c> — through one
    /// engine, so the tests are written in pairs wherever a rule has to hold for each: the manual
    /// protection, the no-price case, the rounding, the permission. A rule that held for an increase
    /// and quietly failed for a decrease is exactly the bug this shape is meant to catch.
    /// </summary>
    [TestFixture]
    public class PriceOperationTests
    {
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

        /// <summary>A drug whose cost and price are given PER SINGLE UNIT, as the item stores them.</summary>
        private int Drug(string name, decimal costPerUnit, decimal? sellPerUnit,
            int strips = 10, int units = 10, bool manual = false)
            => _items.Add(new Item
            {
                NameEn = name, UnitsPerStrip = units, StripsPerBox = strips,
                PurchasePrice = costPerUnit, SellingPrice = sellPerUnit, ManualPrice = manual, IsActive = true
            });

        /// <summary>A one-unit-per-box drug, so a "box price" in a test reads as a plain number.</summary>
        private int Simple(string name, decimal price, decimal cost = 0m, bool manual = false)
            => Drug(name, cost, price, strips: 1, units: 1, manual: manual);

        private PricePlanRow One(int id, PriceOperation op, decimal multiplier,
            bool includeManual = false, IReadOnlyCollection<int> pendingManual = null)
            => _svc.Preview(_admin, new[] { id }, op, multiplier, includeManual, pendingManual).Single();

        // ---------------- the arithmetic ----------------

        [TestCase(100, 1.30, 130)]
        [TestCase(100, 1.20, 120)]
        [TestCase(100, 1.05, 105)]
        [TestCase(5000, 1.30, 6500)]
        public void Increase_MultipliesTheCurrentPrice(double current, double multiplier, double expected)
        {
            PricePlanRow row = One(Simple("d", (decimal)current), PriceOperation.Increase, (decimal)multiplier);
            Assert.That(row.New.BoxPrice, Is.EqualTo((decimal)expected));
            Assert.That(row.Operation, Is.EqualTo(PriceOperation.Increase));
        }

        [TestCase(100, 0.90, 90)]
        [TestCase(100, 0.80, 80)]
        [TestCase(100, 0.70, 70)]
        [TestCase(100, 0.50, 50)]
        [TestCase(200, 0.50, 100)]
        public void Decrease_MultipliesTheCurrentPrice_ItDoesNotSubtract(double current, double multiplier, double expected)
        {
            PricePlanRow row = One(Simple("d", (decimal)current), PriceOperation.Decrease, (decimal)multiplier);

            Assert.That(row.New.BoxPrice, Is.EqualTo((decimal)expected),
                "0.90 means the price KEEPS 90 percent of itself, never 'price minus 0.90'");
            Assert.That(row.Operation, Is.EqualTo(PriceOperation.Decrease));
        }

        // ---------------- which multipliers each operation accepts ----------------

        [Test]
        public void Increase_RefusesAMultiplierBelowOne_AndPointsAtTheOtherButton()
        {
            var ex = Assert.Throws<ValidationException>(
                () => One(Simple("d", 100m), PriceOperation.Increase, 0.90m));
            Assert.That(ex.Message, Does.Contain("تخفيض"));
        }

        [Test]
        public void Decrease_RefusesAMultiplierAboveOne_AndPointsAtTheOtherButton()
        {
            var ex = Assert.Throws<ValidationException>(
                () => One(Simple("d", 100m), PriceOperation.Decrease, 1.20m));
            Assert.That(ex.Message, Does.Contain("زيادة"));
        }

        [Test]
        public void One_IsRefusedForBothOperations_BecauseItChangesNothing()
        {
            int id = Simple("d", 100m);
            Assert.Throws<ValidationException>(() => One(id, PriceOperation.Increase, 1.00m));
            Assert.Throws<ValidationException>(() => One(id, PriceOperation.Decrease, 1.00m));
        }

        [Test]
        public void ZeroAndNegativeAndAbsurd_AreRefusedForBoth()
        {
            int id = Simple("d", 100m);
            foreach (PriceOperation op in new[] { PriceOperation.Increase, PriceOperation.Decrease })
            {
                Assert.Throws<ValidationException>(() => One(id, op, 0m), op.ToString());
                Assert.Throws<ValidationException>(() => One(id, op, -2m), op.ToString());
                Assert.Throws<ValidationException>(() => One(id, op, 500m), op.ToString());
            }
        }

        [Test]
        public void TheManualOperation_CannotBeDrivenByAMultiplier()
        {
            Assert.Throws<ValidationException>(() => One(Simple("d", 100m), PriceOperation.Manual, 1.30m));
        }

        // ---------------- one engine: rounding and packaging behave identically ----------------

        [Test]
        public void BothOperations_RoundTheStrip_AndKeepTheBoxAnExactMultiple()
        {
            _settings.Seed(PricingService.RoundingStepKey, "50");
            int up = Drug("up", 0m, 60m);          // 6,000 a box, 600 a strip
            int down = Drug("down", 0m, 60m);

            PricePlanRow rise = One(up, PriceOperation.Increase, 1.07m);    // 642 a strip
            PricePlanRow fall = One(down, PriceOperation.Decrease, 0.93m);  // 558 a strip

            Assert.That(rise.New.StripPrice, Is.EqualTo(650m));
            Assert.That(fall.New.StripPrice, Is.EqualTo(550m));
            foreach (PricePlanRow r in new[] { rise, fall })
            {
                Assert.That(r.New.BoxPrice, Is.EqualTo(r.New.StripPrice * 10),
                    "box stays an exact multiple of the strip, whichever way the price moved");
                Assert.That(r.New.WasRounded, Is.True);
            }
        }

        [Test]
        public void ARealMultiplierThatRoundsBackToTheCurrentPrice_IsUnchanged()
        {
            // 1,000 × 1.001 = 1,001, which rounds to the nearest 50 and lands back on 1,000.
            PricePlanRow row = One(Simple("d", 1000m), PriceOperation.Increase, 1.001m);
            Assert.That(row.Status, Is.EqualTo(PricePlanStatus.Unchanged));
            Assert.That(row.New.BoxPrice, Is.EqualTo(1000m));
        }

        // ---------------- below cost: calculated and hand-typed alike ----------------

        [Test]
        public void ADecreaseBelowCost_IsFlagged_NotRefused()
        {
            int id = Drug("d", 80m, 100m);          // costs 8,000 a box, sells at 10,000
            PricePlanRow row = One(id, PriceOperation.Decrease, 0.7m);

            Assert.That(row.Status, Is.EqualTo(PricePlanStatus.Planned), "a discount is the pharmacist call");
            Assert.That(row.New.BoxPrice, Is.EqualTo(7000m));
            Assert.That(row.BelowCost, Is.True);
        }

        [Test]
        public void AnIncreaseStaysAboveCost_AndIsNotFlagged()
        {
            Assert.That(One(Drug("d", 50m, 60m), PriceOperation.Increase, 1.3m).BelowCost, Is.False);
        }

        [Test]
        public void AManualPriceBelowCost_IsFlaggedByTheService()
        {
            // Cost 100 a box, the user types 80.
            Item item = _items.GetById(Simple("d", 120m, cost: 100m));

            PricePlanRow below = _svc.PreviewManual(item, 80m);
            Assert.That(below.BelowCost, Is.True, "manual prices are checked against cost, just as calculated ones are");
            Assert.That(below.IsManual, Is.True);
            Assert.That(below.New.BoxPrice, Is.EqualTo(80m), "a typed price is never rounded");
            Assert.That(below.New.WasRounded, Is.False);

            Assert.That(_svc.PreviewManual(item, 150m).BelowCost, Is.False);
        }

        [Test]
        public void CostPerBox_UsesTheSameUnitConversionAsThePrice()
        {
            // purchase_price is stored per SINGLE UNIT (schema: "reference cost / single unit"), so a
            // box's cost is that times the units in a box — the arithmetic UnitConverter.PriceOf does.
            int id = Drug("d", 7m, 9m, strips: 4, units: 12);     // 48 units a box
            PricePlanRow row = One(id, PriceOperation.Increase, 1.1m);

            Assert.That(row.CostPerBox, Is.EqualTo(7m * 48));
            Assert.That(row.CostPerBox, Is.EqualTo(UnitConverter.CostOf(row.Item, UnitType.Box)));
            Assert.That(row.CostPerStrip, Is.EqualTo(7m * 12));
            Assert.That(row.CurrentBoxPrice, Is.EqualTo(9m * 48));
        }

        // ---------------- the manual rule, identical for both operations ----------------

        [TestCase(PriceOperation.Increase, 1.30)]
        [TestCase(PriceOperation.Decrease, 0.90)]
        public void AStoredManualPrice_IsProtected_UnlessIncluded(PriceOperation op, double multiplier)
        {
            int id = Simple("d", 100m, manual: true);

            Assert.That(One(id, op, (decimal)multiplier).Status, Is.EqualTo(PricePlanStatus.ManualSkipped));
            Assert.That(One(id, op, (decimal)multiplier, includeManual: true).Status,
                Is.EqualTo(PricePlanStatus.Planned));
        }

        [TestCase(PriceOperation.Increase, 1.30)]
        [TestCase(PriceOperation.Decrease, 0.90)]
        public void APendingManualPrice_IsProtectedToo_AndTheServiceOwnsThatRule(PriceOperation op, double multiplier)
        {
            // The item is NOT flagged manual in the database — the user typed a price this session and
            // the caller is still holding it. The decision lives in the service, not in the screen.
            int id = Simple("d", 100m);
            var pendingManual = new HashSet<int> { id };

            Assert.That(One(id, op, (decimal)multiplier, false, pendingManual).Status,
                Is.EqualTo(PricePlanStatus.ManualSkipped),
                "a price typed a minute ago must not be silently overwritten");
            Assert.That(One(id, op, (decimal)multiplier, true, pendingManual).Status,
                Is.EqualTo(PricePlanStatus.Planned));
        }

        [TestCase(PriceOperation.Increase, 1.30)]
        [TestCase(PriceOperation.Decrease, 0.90)]
        public void AnItemWithNoSellingPrice_GetsNoPrice_WhicheverWay(PriceOperation op, double multiplier)
        {
            int id = Drug("d", 50m, null);          // a cost, but never priced
            PricePlanRow row = One(id, op, (decimal)multiplier);

            Assert.That(row.Status, Is.EqualTo(PricePlanStatus.NoPrice));
            Assert.That(row.New, Is.Null, "a multiplier must not invent a price out of nothing");
        }

        // ---------------- applying: revalidation, structure, audit ----------------

        private PriceChange ChangeFrom(PricePlanRow row)
            => new PriceChange
            {
                ItemId = row.Item.Id,
                SellingPerUnit = row.New.UnitPrice,
                Operation = row.Operation,
                Multiplier = row.Operation == PriceOperation.Manual ? (decimal?)null : row.Multiplier,
                ExpectedCurrentUnitPrice = row.PricedAt
            };

        [Test]
        public void Apply_WritesThePrice_WhenTheStoredOneIsStillWhatThePreviewSaw()
        {
            int id = Simple("d", 100m);
            PriceApplyResult result = _svc.Apply(_admin, new[] { ChangeFrom(One(id, PriceOperation.Increase, 1.30m)) });

            Assert.That(result.Applied, Is.EqualTo(1));
            Assert.That(result.Failures, Is.Empty);
            Assert.That(UnitConverter.PriceOf(_items.GetById(id), UnitType.Box), Is.EqualTo(130m));
        }

        [Test]
        public void Apply_RefusesAChange_WhenAnotherTerminalMovedThePriceSinceThePreview()
        {
            int id = Simple("d", 100m);
            PriceChange change = ChangeFrom(One(id, PriceOperation.Increase, 1.30m));

            // Someone else reprices it — a delivery lands, or another manager edits it.
            _items.ApplySellingPrice(id, 200m, manual: false);

            PriceApplyResult result = _svc.Apply(_admin, new[] { change });

            Assert.That(result.Applied, Is.Zero);
            Assert.That(result.Failures.Single().ItemId, Is.EqualTo(id));
            Assert.That(result.Failures.Single().Reason, Does.Contain("جهاز آخر"));
            Assert.That(_items.GetById(id).SellingPrice, Is.EqualTo(200m), "their change stands");
        }

        [Test]
        public void Apply_ReportsFailuresStructured_SoACallerNeedNotParseText()
        {
            int ok = Simple("ok", 100m);

            PriceApplyResult result = _svc.Apply(_admin, new[]
            {
                new PriceChange { ItemId = 999999, SellingPerUnit = 130m, Operation = PriceOperation.Increase },
                new PriceChange { ItemId = ok, SellingPerUnit = 130m, Operation = PriceOperation.Increase },
                new PriceChange { ItemId = ok, SellingPerUnit = -1m, Operation = PriceOperation.Increase }
            });

            Assert.That(result.Applied, Is.EqualTo(1), "the good one still went through");
            Assert.That(result.FailedItemIds, Is.EquivalentTo(new[] { 999999, ok }));
            Assert.That(result.Failures.All(f => !string.IsNullOrWhiteSpace(f.Reason)));
        }

        [Test]
        public void Apply_AuditsTheOperationAndTheMultiplier()
        {
            int up = Simple("up", 100m), down = Simple("down", 100m), hand = Simple("hand", 100m);

            _svc.Apply(_admin, new[]
            {
                new PriceChange { ItemId = up, SellingPerUnit = 130m, Operation = PriceOperation.Increase, Multiplier = 1.30m },
                new PriceChange { ItemId = down, SellingPerUnit = 90m, Operation = PriceOperation.Decrease, Multiplier = 0.90m },
                new PriceChange { ItemId = hand, SellingPerUnit = 111m, Operation = PriceOperation.Manual }
            });

            Assert.That(_audit.Entries.Any(e => e.Action == "PriceIncrease" && e.EntityId == up));
            Assert.That(_audit.Entries.Any(e => e.Action == "PriceDecrease" && e.EntityId == down));
            Assert.That(_audit.Entries.Any(e => e.Action == "ManualPrice" && e.EntityId == hand));
            Assert.That(_audit.Entries.First(e => e.EntityId == down).Details,
                Does.Contain("تخفيض").And.Contain("0.90"));
        }

        [Test]
        public void Apply_MarksAManualPriceAsManual_AndACalculatedOneAsNot()
        {
            int calc = Simple("calc", 100m), hand = Simple("hand", 100m);
            _svc.Apply(_admin, new[]
            {
                new PriceChange { ItemId = calc, SellingPerUnit = 130m, Operation = PriceOperation.Increase, Multiplier = 1.30m },
                new PriceChange { ItemId = hand, SellingPerUnit = 111m, Operation = PriceOperation.Manual }
            });

            Assert.That(_items.GetById(calc).ManualPrice, Is.False);
            Assert.That(_items.GetById(hand).ManualPrice, Is.True, "so a later multiplier leaves it alone");
        }

        // ---------------- permission, on every door ----------------

        [Test]
        public void ACashier_MayNotPreviewIncreaseOrDecreaseOrManual_NorApply()
        {
            int id = Simple("d", 100m);

            Assert.Throws<PermissionDeniedException>(() => _svc.Preview(_cashier, new[] { id }, PriceOperation.Increase, 1.3m));
            Assert.Throws<PermissionDeniedException>(() => _svc.Preview(_cashier, new[] { id }, PriceOperation.Decrease, 0.9m));
            Assert.Throws<PermissionDeniedException>(() => _svc.PreviewManual(_cashier, id, 50m));
            Assert.Throws<PermissionDeniedException>(() => _svc.Search(_cashier, ""));
            Assert.Throws<PermissionDeniedException>(() => _svc.Apply(_cashier,
                new[] { new PriceChange { ItemId = id, SellingPerUnit = 130m, Operation = PriceOperation.Increase } }));

            Assert.That(_items.GetById(id).SellingPrice, Is.EqualTo(100m), "and nothing moved");
        }

        [Test]
        public void APrivilegedEmployee_MayDoBothOperations()
        {
            int id = Simple("d", 100m);
            Assert.DoesNotThrow(() => _svc.Preview(_priv, new[] { id }, PriceOperation.Increase, 1.3m));
            Assert.DoesNotThrow(() => _svc.Preview(_priv, new[] { id }, PriceOperation.Decrease, 0.9m));
            Assert.That(_svc.Apply(_priv, new[] { new PriceChange
            {
                ItemId = id, SellingPerUnit = 90m, Operation = PriceOperation.Decrease, Multiplier = 0.9m
            }}).Applied, Is.EqualTo(1));
        }

        // ---------------- what the screen shows ----------------

        [Test]
        public void Describe_TurnsABareMultiplierIntoSomethingReadable()
        {
            Assert.That(PriceOperations.Describe(PriceOperation.Increase, 1.30m), Is.EqualTo("زيادة 30%"));
            Assert.That(PriceOperations.Describe(PriceOperation.Decrease, 0.90m), Is.EqualTo("تخفيض 10%"));
            Assert.That(PriceOperations.Describe(PriceOperation.Decrease, 0.50m), Is.EqualTo("تخفيض 50%"));
        }
    }
}
