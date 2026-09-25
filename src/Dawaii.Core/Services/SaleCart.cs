using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>One drug on a customer's invoice-in-progress, in whatever unit they are buying it in.</summary>
    public sealed class CartRow
    {
        public Item Item;
        public UnitType Unit = UnitType.Box;
        public int Qty = 1;

        public decimal UnitPrice => UnitConverter.PriceOf(Item, Unit);
        public decimal Total => UnitConverter.LineTotal(Item, Qty, Unit);
    }

    /// <summary>
    /// One customer's invoice-in-progress.
    ///
    /// Nothing here has been sold. Stock is allocated only when the invoice is completed, which is why
    /// two carts holding the same drug are as safe as one.
    /// </summary>
    public sealed class SaleCart
    {
        /// <summary>The tab's number, stable for the cart's life.</summary>
        public int Number;

        public readonly List<CartRow> Lines = new List<CartRow>();
        public Customer CreditCustomer;
        public decimal Discount;

        /// <summary>Which payment option the cashier picked, and the expiry warning they were shown.
        /// Remembered per cart so switching away and back puts the screen back as it was.</summary>
        public int PaymentIndex;
        public string Warn = "";

        public decimal Subtotal => Lines.Sum(r => r.Total);

        /// <summary>
        /// What the customer pays. A discount larger than the invoice is capped rather than refused —
        /// the cashier is mid-sale with someone waiting, and a till that hands money back because a
        /// digit was mistyped would be worse than one that simply charges nothing.
        /// </summary>
        public decimal Total => Subtotal - (Discount > Subtotal ? Subtotal : Discount);

        public bool IsEmpty => Lines.Count == 0;

        /// <summary>The cart as the sale engine wants it.</summary>
        public List<CartLine> ToSaleLines()
            => Lines.Select(r => new CartLine { ItemId = r.Item.Id, UnitType = r.Unit, Quantity = r.Qty }).ToList();
    }

    /// <summary>
    /// The several invoices a counter has open at once (V2.3, moved out of the screen in V2.4).
    ///
    /// A customer who is still deciding must not hold up the next one in the queue, so the till keeps
    /// several carts and shows one of them. Which cart is showing, what a new one is numbered and
    /// where the screen lands after one closes are rules, not drawing — and while they lived in the
    /// WinForms control none of them could be tested without standing a whole screen up.
    ///
    /// There is always exactly one active cart. A till with no cart on it is not a state the cashier
    /// can do anything with, so closing the last one opens a fresh empty one in its place.
    /// </summary>
    public sealed class CartBook
    {
        private readonly List<SaleCart> _carts = new List<SaleCart>();
        private SaleCart _active;

        public CartBook() { Reset(); }

        public IReadOnlyList<SaleCart> All => _carts;
        public int Count => _carts.Count;

        /// <summary>The invoice on screen. Never null — see the note on the class.</summary>
        public SaleCart Active => _active;

        /// <summary>
        /// Forgets every pending invoice and opens a clean one — called at logout, so the next cashier
        /// starts fresh.
        ///
        /// The first cart is made here rather than on first use. While it was lazy, Count and All
        /// reported an empty book until something happened to read Active, so a caller that opened a
        /// new invoice first got number 1 twice over.
        /// </summary>
        public void Reset()
        {
            _carts.Clear();
            _active = new SaleCart { Number = 1 };
            _carts.Add(_active);
        }

        /// <summary>
        /// Opens a new empty invoice and switches to it. The number is the lowest free one, so after
        /// [1][2][3] → complete 2 → [1][3], the next customer is [2] again, not [4]. The strip is what
        /// the cashier navigates by, and it should stay short rather than counting up all day.
        /// </summary>
        public SaleCart OpenNew()
        {
            int number = 1;
            while (_carts.Any(c => c.Number == number)) number++;

            var cart = new SaleCart { Number = number };
            _carts.Add(cart);
            _active = cart;
            return cart;
        }

        /// <summary>Switches to a cart of this book. Returns false for one that is not in it.</summary>
        public bool SwitchTo(SaleCart cart)
        {
            if (cart == null || !_carts.Contains(cart)) return false;
            _active = cart;
            return true;
        }

        public bool SwitchToNumber(int number) => SwitchTo(_carts.FirstOrDefault(c => c.Number == number));

        /// <summary>Moves to the next cart along, wrapping round. Does nothing when there is only one.</summary>
        public bool SwitchToNext()
        {
            if (_carts.Count < 2) return false;
            int i = _carts.IndexOf(Active);
            _active = _carts[(i + 1) % _carts.Count];
            return true;
        }

        /// <summary>
        /// Drops the active invoice — completed or abandoned — and moves to a neighbour: the one to its
        /// right in the strip, else the one to its left, else a fresh empty one. The screen must never
        /// sit on a tab that no longer exists, and must never show one customer's cart under another
        /// customer's number.
        /// </summary>
        /// <returns>The cart now active.</returns>
        public SaleCart CloseActive()
        {
            SaleCart closing = Active;
            int i = _carts.IndexOf(closing);
            _carts.Remove(closing);

            if (_carts.Count == 0) _carts.Add(new SaleCart { Number = 1 });

            _active = _carts[i < _carts.Count ? i : _carts.Count - 1];
            return _active;
        }
    }
}
