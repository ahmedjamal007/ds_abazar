using System;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Proves the days-until-expiry count uses real calendar dates, so months of different lengths
    /// (28/29/30/31 days) and leap years are handled correctly — it is never a naive "30 days/month".
    /// </summary>
    [TestFixture]
    public class ExpiryEvaluatorTests
    {
        private static StockBatch Batch(DateTime expiry) => new StockBatch { Id = 1, ItemId = 1, QuantityUnits = 10, ExpiryDate = expiry };

        [Test]
        public void DaysUntilExpiry_CountsRealCalendarDays_AcrossMonthsOfDifferentLengths()
        {
            // 2027-07-01 → 2027-09-01: July (31) + August (31) = 62 real days, NOT 60 (2×30).
            var asOf = new DateTime(2027, 7, 1);
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(Batch(new DateTime(2027, 9, 1)), asOf), Is.EqualTo(62));
        }

        [Test]
        public void DaysUntilExpiry_HandlesFebruary_LeapAndNonLeap()
        {
            // Non-leap February has 28 days...
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(Batch(new DateTime(2027, 3, 1)), new DateTime(2027, 2, 1)), Is.EqualTo(28));
            // ...and leap-year February (2028) has 29.
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(Batch(new DateTime(2028, 3, 1)), new DateTime(2028, 2, 1)), Is.EqualTo(29));
        }

        [Test]
        public void DaysUntilExpiry_IgnoresTimeOfDay()
        {
            var asOf = new DateTime(2027, 7, 1, 23, 59, 0);
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(Batch(new DateTime(2027, 7, 3, 0, 1, 0)), asOf), Is.EqualTo(2));
        }

        [Test]
        public void ExpiredBatch_IsNegativeAndFlaggedExpired()
        {
            var asOf = new DateTime(2027, 7, 10);
            var batch = Batch(new DateTime(2027, 7, 1));
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(batch, asOf), Is.EqualTo(-9));
            Assert.That(ExpiryEvaluator.IsExpired(batch, asOf), Is.True);
        }

        [Test]
        public void NearExpiry_Within90DayWindow_IncludesExactlyOnBoundary()
        {
            var asOf = new DateTime(2027, 7, 1);
            // 2027-07-01 → 2027-09-29 is exactly 90 days (Jul 31 + Aug 31 + 28) — on the boundary.
            Assert.That(ExpiryEvaluator.DaysUntilExpiry(Batch(new DateTime(2027, 9, 29)), asOf), Is.EqualTo(90));
            Assert.That(ExpiryEvaluator.IsNearExpiry(Batch(new DateTime(2027, 9, 29)), asOf, 90), Is.True);
            Assert.That(ExpiryEvaluator.IsNearExpiry(Batch(new DateTime(2027, 9, 30)), asOf, 90), Is.False);
        }
    }
}
