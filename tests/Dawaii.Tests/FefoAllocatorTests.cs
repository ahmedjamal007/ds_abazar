using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class FefoAllocatorTests
    {
        private static StockBatch Batch(int id, int qty, DateTime? expiry, decimal cost = 1m, bool disposed = false)
            => new StockBatch { Id = id, ItemId = 1, QuantityUnits = qty, ExpiryDate = expiry, BoxPurchasePrice = cost, StripsPerBox = 1, UnitsPerStrip = 1, IsDisposed = disposed };

        [Test]
        public void Allocate_TakesNearestExpiryFirst()
        {
            var batches = new List<StockBatch>
            {
                Batch(1, 10, new DateTime(2027, 1, 1), 2m),
                Batch(2, 10, new DateTime(2026, 1, 1), 1m)  // expires sooner -> consumed first
            };

            var alloc = FefoAllocator.Allocate(1, batches, 5);

            Assert.That(alloc.Count, Is.EqualTo(1));
            Assert.That(alloc[0].BatchId, Is.EqualTo(2));
            Assert.That(alloc[0].Units, Is.EqualTo(5));
            Assert.That(alloc[0].UnitCost, Is.EqualTo(1m));
        }

        [Test]
        public void Allocate_SpillsAcrossBatchesInOrder()
        {
            var batches = new List<StockBatch>
            {
                Batch(1, 8, new DateTime(2026, 6, 1)),
                Batch(2, 8, new DateTime(2026, 12, 1))
            };

            var alloc = FefoAllocator.Allocate(1, batches, 12);

            Assert.That(alloc.Select(a => a.BatchId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(alloc[0].Units, Is.EqualTo(8));
            Assert.That(alloc[1].Units, Is.EqualTo(4));
        }

        [Test]
        public void Allocate_NullExpiry_SortsAfterDatedBatches()
        {
            var batches = new List<StockBatch>
            {
                Batch(1, 5, null),                          // no expiry -> last
                Batch(2, 5, new DateTime(2030, 1, 1))       // dated -> first even though far off
            };

            var alloc = FefoAllocator.Allocate(1, batches, 5);

            Assert.That(alloc[0].BatchId, Is.EqualTo(2));
        }

        [Test]
        public void Allocate_SkipsDisposedBatches()
        {
            var batches = new List<StockBatch>
            {
                Batch(1, 100, new DateTime(2026, 1, 1), disposed: true),
                Batch(2, 5, new DateTime(2027, 1, 1))
            };

            var alloc = FefoAllocator.Allocate(1, batches, 5);
            Assert.That(alloc.Single().BatchId, Is.EqualTo(2));
        }

        [Test]
        public void Allocate_InsufficientStock_Throws()
        {
            var batches = new List<StockBatch> { Batch(1, 3, new DateTime(2027, 1, 1)) };

            var ex = Assert.Throws<InsufficientStockException>(() => FefoAllocator.Allocate(1, batches, 10));
            Assert.That(ex.AvailableUnits, Is.EqualTo(3));
            Assert.That(ex.RequestedUnits, Is.EqualTo(10));
        }

        [Test]
        public void Allocate_ZeroOrNegative_Throws()
        {
            var batches = new List<StockBatch> { Batch(1, 3, null) };
            Assert.Throws<ValidationException>(() => FefoAllocator.Allocate(1, batches, 0));
        }

        [Test]
        public void AvailableUnits_ExcludesDisposed()
        {
            var batches = new List<StockBatch>
            {
                Batch(1, 10, null),
                Batch(2, 5, null, disposed: true)
            };
            Assert.That(FefoAllocator.AvailableUnits(batches), Is.EqualTo(10));
        }

        [Test]
        public void Allocate_SellingLastUnit_LeavesZeroAvailable()
        {
            // Acceptance §6.5 building block: selling the last unit empties stock.
            var batches = new List<StockBatch> { Batch(1, 1, new DateTime(2027, 1, 1)) };

            var alloc = FefoAllocator.Allocate(1, batches, 1);
            batches[0].QuantityUnits -= alloc[0].Units;

            Assert.That(FefoAllocator.AvailableUnits(batches), Is.EqualTo(0));
        }
    }
}
