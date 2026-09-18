using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class ReportCalculatorTests
    {
        private static Sale Sale(decimal total, decimal cost, SaleType type, SaleStatus status = SaleStatus.Completed)
            => new Sale { Total = total, CostTotal = cost, SaleType = type, Status = status, CreatedAt = DateTime.Today };

        [Test]
        public void BuildDaily_AggregatesTotalsCashCreditAndCount()
        {
            var sales = new List<Sale>
            {
                Sale(200m, 120m, SaleType.Cash),
                Sale(50m, 30m, SaleType.Credit),
                Sale(100m, 60m, SaleType.Cash),
            };

            var rep = ReportCalculator.BuildDaily(DateTime.Today, sales, includeProfit: true);

            Assert.That(rep.TransactionCount, Is.EqualTo(3));
            Assert.That(rep.TotalSales, Is.EqualTo(350m));
            Assert.That(rep.CashTotal, Is.EqualTo(300m));
            Assert.That(rep.CreditTotal, Is.EqualTo(50m));
            Assert.That(rep.TotalProfit, Is.EqualTo(140m)); // (200-120)+(50-30)+(100-60)=80+20+40
        }

        [Test]
        public void BuildDaily_ExcludesReturnedFromTotals_CountsThemSeparately()
        {
            var sales = new List<Sale>
            {
                Sale(200m, 120m, SaleType.Cash),
                Sale(80m, 50m, SaleType.Cash, SaleStatus.Returned),
            };

            var rep = ReportCalculator.BuildDaily(DateTime.Today, sales, includeProfit: true);

            Assert.That(rep.TotalSales, Is.EqualTo(200m));
            Assert.That(rep.TransactionCount, Is.EqualTo(1));
            Assert.That(rep.ReturnedCount, Is.EqualTo(1));
            Assert.That(rep.ReturnedTotal, Is.EqualTo(80m));
        }

        [Test]
        public void BuildDaily_NonAdmin_HidesProfit_FR_RPT_05()
        {
            var sales = new List<Sale> { Sale(200m, 120m, SaleType.Cash) };
            var rep = ReportCalculator.BuildDaily(DateTime.Today, sales, includeProfit: false);

            Assert.That(rep.ProfitVisible, Is.False);
            Assert.That(rep.TotalProfit, Is.EqualTo(0m));
            Assert.That(rep.TotalSales, Is.EqualTo(200m), "Sales totals stay visible to cashiers.");
        }
    }

    [TestFixture]
    public class BackupPolicyTests
    {
        private readonly DateTime _now = new DateTime(2026, 7, 9);

        [Test]
        public void NeedsWarning_NoBackup_True()
            => Assert.That(BackupPolicy.NeedsWarning(null, _now), Is.True);

        [Test]
        public void NeedsWarning_RecentBackup_False()
            => Assert.That(BackupPolicy.NeedsWarning(_now.AddDays(-1), _now), Is.False);

        [Test]
        public void NeedsWarning_ThreeDaysOld_True()
            => Assert.That(BackupPolicy.NeedsWarning(_now.AddDays(-3), _now), Is.True);

        [Test]
        public void ToPrune_KeepsNewestN()
        {
            var files = new[]
            {
                ("a", new DateTime(2026, 7, 1)),
                ("b", new DateTime(2026, 7, 5)),
                ("c", new DateTime(2026, 7, 9)),
                ("d", new DateTime(2026, 7, 3)),
            };

            var prune = BackupPolicy.ToPrune(files, x => x.Item2, keepLast: 2).Select(x => x.Item1).ToList();

            Assert.That(prune, Is.EquivalentTo(new[] { "a", "d" })); // keep c (9th) and b (5th)
        }

        [Test]
        public void ToPrune_KeepAll_WhenFewerThanN()
        {
            var files = new[] { ("a", DateTime.Today) };
            Assert.That(BackupPolicy.ToPrune(files, x => x.Item2, keepLast: 5), Is.Empty);
        }
    }
}
