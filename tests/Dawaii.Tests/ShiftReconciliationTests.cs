using System.Collections.Generic;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Counting the drawer at the end of a shift (V2.4).
    ///
    /// This is the number a pharmacist checks their cash against, so the tests are written the way
    /// they would check it: money that went into the drawer, less every way money left it. Everything
    /// that did NOT touch the drawer — a card payment, a credit sale, medicine taken by staff — has
    /// its own test, because each one of those wrongly included makes the cashier look short or over
    /// by exactly that amount, and that is an accusation.
    ///
    /// The arithmetic used to live in the WinForms screen, where none of this could be tested.
    /// </summary>
    [TestFixture]
    public class ShiftReconciliationTests
    {
        private static Sale Paid(decimal total, string method, decimal returned = 0m)
            => new Sale
            {
                Total = total, ReturnedTotal = returned, PaymentMethod = method,
                SaleType = SaleType.Cash, Status = SaleStatus.Completed
            };

        private static ShiftTill Till(params Sale[] sales)
            => ShiftReconciliation.Build(sales, new List<EmployeeDayRow>(), 0m, 0m);

        // ---------------- what came in ----------------

        [Test]
        public void CashSales_LandInTheDrawer()
        {
            ShiftTill till = Till(Paid(100m, PaymentMethods.Cash), Paid(50m, PaymentMethods.Cash));

            Assert.That(till.Cash, Is.EqualTo(150m));
            Assert.That(till.ExpectedCash, Is.EqualTo(150m));
        }

        [Test]
        public void CardAndWalletSales_AreCountedButNotInTheDrawer()
        {
            ShiftTill till = Till(
                Paid(100m, PaymentMethods.Cash),
                Paid(200m, PaymentMethods.Bankak),
                Paid(30m, PaymentMethods.Fawry),
                Paid(40m, PaymentMethods.Ocash));

            Assert.That(till.Bankak, Is.EqualTo(200m));
            Assert.That(till.Fawry, Is.EqualTo(30m));
            Assert.That(till.Ocash, Is.EqualTo(40m));
            Assert.That(till.ExpectedCash, Is.EqualTo(100m),
                "that money went to an account — asking the cashier to produce it would be an accusation");
        }

        [Test]
        public void CreditSales_PutNothingInTheDrawer()
        {
            var credit = new Sale
            {
                Total = 500m, SaleType = SaleType.Credit, Status = SaleStatus.Completed,
                PaymentMethod = PaymentMethods.Cash        // a stray value must not matter
            };

            ShiftTill till = Till(Paid(100m, PaymentMethods.Cash), credit);

            Assert.That(till.ExpectedCash, Is.EqualTo(100m), "nobody paid for it yet");
        }

        [Test]
        public void AFullyReturnedSale_CountsForNothing()
        {
            var returned = new Sale
            {
                Total = 100m, PaymentMethod = PaymentMethods.Cash,
                SaleType = SaleType.Cash, Status = SaleStatus.Returned
            };

            Assert.That(Till(Paid(60m, PaymentMethods.Cash), returned).ExpectedCash, Is.EqualTo(60m));
        }

        [Test]
        public void APartlyReturnedSale_CountsForWhatItActuallyLeftBehind()
        {
            // Sold 100, gave 30 back over the counter: 70 is in the drawer.
            Assert.That(Till(Paid(100m, PaymentMethods.Cash, returned: 30m)).Cash, Is.EqualTo(70m));
        }

        // ---------------- what went out ----------------

        [Test]
        public void MoneyAdvancedToStaff_ComesOutOfTheDrawer()
        {
            ShiftTill till = ShiftReconciliation.Build(
                new[] { Paid(500m, PaymentMethods.Cash) },
                new[] { new EmployeeDayRow { MoneyExpenses = 80m }, new EmployeeDayRow { MoneyExpenses = 20m } },
                0m, 0m);

            Assert.That(till.MoneyExpenses, Is.EqualTo(100m));
            Assert.That(till.ExpectedCash, Is.EqualTo(400m));
        }

        [Test]
        public void MedicineTakenByStaff_IsAnExpenseButNotACashOne()
        {
            ShiftTill till = ShiftReconciliation.Build(
                new[] { Paid(500m, PaymentMethods.Cash) },
                new[] { new EmployeeDayRow { MoneyExpenses = 50m, MedicineExpenses = 70m } },
                0m, 0m);

            Assert.That(till.AllExpenses, Is.EqualTo(120m), "both are expenses");
            Assert.That(till.ExpectedCash, Is.EqualTo(450m),
                "but no one took 70 out of the drawer — a box left the shelf");
        }

        [Test]
        public void CounterPurchases_ComeOutOfTheDrawer()
        {
            ShiftTill till = ShiftReconciliation.Build(
                new[] { Paid(500m, PaymentMethods.Cash) }, new List<EmployeeDayRow>(),
                purchases: 120m, supplierCash: 0m);

            Assert.That(till.ExpectedCash, Is.EqualTo(380m));
        }

        [Test]
        public void CashPaidToASupplierAtTheDoor_ComesOutOfTheDrawer()
        {
            // The outflow the formula did not know about for two versions: the cashier came up short
            // by exactly what had been handed to the distributor.
            ShiftTill till = ShiftReconciliation.Build(
                new[] { Paid(500m, PaymentMethods.Cash) }, new List<EmployeeDayRow>(),
                purchases: 0m, supplierCash: 200m);

            Assert.That(till.SupplierCash, Is.EqualTo(200m));
            Assert.That(till.ExpectedCash, Is.EqualTo(300m));
        }

        [Test]
        public void AWholeShift_AddsUpTheWayAPharmacistWouldCountIt()
        {
            ShiftTill till = ShiftReconciliation.Build(
                new[]
                {
                    Paid(1000m, PaymentMethods.Cash),
                    Paid(400m, PaymentMethods.Cash, returned: 150m),
                    Paid(600m, PaymentMethods.Bankak),
                    new Sale { Total = 300m, SaleType = SaleType.Credit, Status = SaleStatus.Completed },
                },
                new[] { new EmployeeDayRow { MoneyExpenses = 90m, MedicineExpenses = 40m } },
                purchases: 110m,
                supplierCash: 250m);

            // In:  1000 + (400 − 150) = 1250 cash.  Out: 90 staff + 110 purchases + 250 supplier = 450.
            Assert.That(till.Cash, Is.EqualTo(1250m));
            Assert.That(till.ExpectedCash, Is.EqualTo(800m));
            Assert.That(till.Bankak, Is.EqualTo(600m), "banked, not in the drawer");
            Assert.That(till.AllExpenses, Is.EqualTo(130m));
        }

        [Test]
        public void AnEmptyShift_IsZero_NotACrash()
        {
            ShiftTill till = ShiftReconciliation.Build(null, null, 0m, 0m);

            Assert.That(till.Cash, Is.Zero);
            Assert.That(till.ExpectedCash, Is.Zero);
            Assert.That(till.AllExpenses, Is.Zero);
        }
    }
}
