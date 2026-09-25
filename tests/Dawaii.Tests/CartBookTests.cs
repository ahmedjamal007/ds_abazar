using System.Linq;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The several invoices a counter has open at once (V2.3, moved out of the screen in V2.4).
    ///
    /// These rules decide which customer's cart the cashier is looking at, so getting them wrong does
    /// not throw — it quietly rings one customer's medicines up against another's invoice. They used
    /// to be reachable only by standing up a whole WinForms screen; now they are a few lines each.
    /// </summary>
    [TestFixture]
    public class CartBookTests
    {
        private static Item Drug(int id, decimal perUnit)
            => new Item { Id = id, NameEn = "drug" + id, UnitsPerStrip = 10, StripsPerBox = 10,
                          SellingPrice = perUnit, IsActive = true };

        // ---------------- there is always a cart ----------------

        [Test]
        public void AFreshBook_AlreadyHasAnInvoiceOpen()
        {
            var book = new CartBook();

            Assert.That(book.Active, Is.Not.Null, "a till with no cart on it is not a state a cashier can use");
            Assert.That(book.Active.Number, Is.EqualTo(1));
            Assert.That(book.Count, Is.EqualTo(1));
        }

        [Test]
        public void ClosingTheLastInvoice_LeavesAFreshEmptyOne()
        {
            var book = new CartBook();
            book.Active.Lines.Add(new CartRow { Item = Drug(1, 60m) });

            SaleCart now = book.CloseActive();

            Assert.That(book.Count, Is.EqualTo(1));
            Assert.That(now.Number, Is.EqualTo(1));
            Assert.That(now.IsEmpty, Is.True, "the next customer starts clean");
        }

        // ---------------- numbering ----------------

        [Test]
        public void ANewInvoice_TakesTheLowestFreeNumber()
        {
            var book = new CartBook();
            book.OpenNew();                 // 2
            book.OpenNew();                 // 3
            Assert.That(book.All.Select(c => c.Number), Is.EqualTo(new[] { 1, 2, 3 }));

            book.SwitchToNumber(2);
            book.CloseActive();             // finish customer 2

            Assert.That(book.OpenNew().Number, Is.EqualTo(2),
                "the strip is what the cashier navigates by — it should stay short, not count up all day");
        }

        // ---------------- switching ----------------

        [Test]
        public void SwitchingByNumber_LandsOnThatInvoice()
        {
            var book = new CartBook();
            book.OpenNew();
            book.OpenNew();

            Assert.That(book.SwitchToNumber(1), Is.True);
            Assert.That(book.Active.Number, Is.EqualTo(1));
        }

        [Test]
        public void SwitchingToANumberThatIsNotOpen_ChangesNothing()
        {
            var book = new CartBook();
            book.OpenNew();                 // active is 2

            Assert.That(book.SwitchToNumber(9), Is.False);
            Assert.That(book.Active.Number, Is.EqualTo(2), "the cashier stays where they were");
        }

        [Test]
        public void SwitchingToACartFromAnotherTill_IsRefused()
        {
            var book = new CartBook();
            var foreign = new SaleCart { Number = 1 };

            Assert.That(book.SwitchTo(foreign), Is.False);
            Assert.That(book.Active, Is.Not.SameAs(foreign));
        }

        [Test]
        public void TheNextInvoice_WrapsRoundTheStrip()
        {
            var book = new CartBook();
            book.OpenNew();                 // 2
            book.OpenNew();                 // 3, active

            book.SwitchToNext();
            Assert.That(book.Active.Number, Is.EqualTo(1), "past the end comes back to the start");

            book.SwitchToNext();
            Assert.That(book.Active.Number, Is.EqualTo(2));
        }

        [Test]
        public void WithOnlyOneInvoice_ThereIsNoNextToGoTo()
        {
            var book = new CartBook();
            Assert.That(book.SwitchToNext(), Is.False);
            Assert.That(book.Active.Number, Is.EqualTo(1));
        }

        // ---------------- where the screen lands after a close ----------------

        [Test]
        public void ClosingAnInvoice_MovesToTheNeighbourOnItsRight()
        {
            var book = new CartBook();
            book.OpenNew();                 // 2
            book.OpenNew();                 // 3
            book.SwitchToNumber(2);

            Assert.That(book.CloseActive().Number, Is.EqualTo(3));
        }

        [Test]
        public void ClosingTheLastInvoiceInTheStrip_MovesToItsLeft()
        {
            var book = new CartBook();
            book.OpenNew();                 // 2
            book.OpenNew();                 // 3, active and rightmost

            Assert.That(book.CloseActive().Number, Is.EqualTo(2),
                "the screen must never sit on a tab that no longer exists");
        }

        [Test]
        public void Logout_ForgetsEveryPendingInvoice()
        {
            var book = new CartBook();
            book.OpenNew();
            book.Active.Lines.Add(new CartRow { Item = Drug(1, 60m) });

            book.Reset();

            Assert.That(book.Active.Number, Is.EqualTo(1));
            Assert.That(book.Count, Is.EqualTo(1));
            Assert.That(book.Active.IsEmpty, Is.True,
                "a pending invoice belongs to the cashier who typed it, not the next one");
        }

        // ---------------- what the customer pays ----------------

        [Test]
        public void TheTotal_IsTheLinesLessTheDiscount()
        {
            var cart = new SaleCart();
            cart.Lines.Add(new CartRow { Item = Drug(1, 60m), Unit = UnitType.Strip, Qty = 2 });  // 1,200
            cart.Lines.Add(new CartRow { Item = Drug(2, 50m), Unit = UnitType.Unit, Qty = 4 });   //   200
            cart.Discount = 400m;

            Assert.That(cart.Subtotal, Is.EqualTo(1400m));
            Assert.That(cart.Total, Is.EqualTo(1000m));
        }

        [Test]
        public void ADiscountBiggerThanTheInvoice_ChargesNothing_RatherThanHandingMoneyBack()
        {
            var cart = new SaleCart();
            cart.Lines.Add(new CartRow { Item = Drug(1, 60m), Unit = UnitType.Strip, Qty = 1 });  // 600
            cart.Discount = 5000m;                                                                // mistyped

            Assert.That(cart.Total, Is.Zero, "a till that pays the customer over a stray digit is worse");
        }

        [Test]
        public void TheCart_HandsTheSaleEngineWhatItAsksFor()
        {
            var cart = new SaleCart();
            cart.Lines.Add(new CartRow { Item = Drug(7, 60m), Unit = UnitType.Strip, Qty = 3 });

            CartLine line = cart.ToSaleLines().Single();

            Assert.That(line.ItemId, Is.EqualTo(7));
            Assert.That(line.UnitType, Is.EqualTo(UnitType.Strip));
            Assert.That(line.Quantity, Is.EqualTo(3));
        }
    }
}
