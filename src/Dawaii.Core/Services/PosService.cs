using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>Point-of-sale orchestration: builds and commits sales, handles returns (FR-POS-*).</summary>
    public class PosService
    {
        private readonly IItemRepository _items;
        private readonly IStockRepository _stock;
        private readonly ISaleStore _sales;
        private readonly ICustomerRepository _customers;
        private readonly ISettingsRepository _settings;
        private readonly IAuditRepository _audit;

        public PosService(IItemRepository items, IStockRepository stock, ISaleStore sales,
            ICustomerRepository customers, ISettingsRepository settings, IAuditRepository audit)
        {
            _items = items;
            _stock = stock;
            _sales = sales;
            _customers = customers;
            _settings = settings;
            _audit = audit;
        }

        /// <summary>Builds the sale from the cart and commits it in one transaction (FR-POS-05).</summary>
        public Sale Complete(User user, IList<CartLine> cart, SaleType saleType,
            int? customerId, decimal discount, string terminal, string paymentMethod = null)
        {
            if (user == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            if (saleType == SaleType.Credit)
            {
                if (customerId == null) throw new ValidationException("اختر عميلاً للبيع بالآجل.");
                if (_customers.GetById(customerId.Value) == null)
                    throw new ValidationException("العميل غير موجود.");
            }

            var header = new SaleHeader
            {
                UserId = user.Id,
                SaleType = saleType,
                CustomerId = saleType == SaleType.Credit ? customerId : null,
                Discount = discount,
                Terminal = terminal
            };

            Sale built = SaleBuilder.Build(cart, _items.GetById, id => _stock.GetSellableBatches(id), header);
            // Credit sales settle through the debt ledger, so payment method only applies to non-credit (V1.3).
            built.PaymentMethod = saleType == SaleType.Credit ? null : (paymentMethod ?? "Cash");
            Sale saved = _sales.Save(built);   // atomic: stock + debt + audit handled by the store

            return saved;
        }

        /// <summary>
        /// Cancels/returns a whole sale (FR-POS-08). Owner decision D-15: employees handle
        /// returns at the counter too, so any logged-in user may return; the audit log records who.
        /// </summary>
        public Return ReturnSale(User user, int saleId, string reason)
        {
            if (user == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            Sale sale = _sales.GetById(saleId);
            if (sale == null) throw new ValidationException("الفاتورة غير موجودة.");
            if (sale.Status == SaleStatus.Returned) throw new ValidationException("الفاتورة مُرجعة بالفعل.");
            return _sales.ReturnSale(saleId, user.Id, reason);
        }

        /// <summary>
        /// Returns part of a sale (FR-POS-08): the listed quantities go back to the batches they were
        /// sold from and the customer is refunded their share of the invoice, which stays open for
        /// whatever they kept. A customer who bought 50 boxes and brings 5 back is the everyday case.
        /// </summary>
        public Return ReturnItems(User user, int saleId, IList<ReturnRequest> requests, string reason)
        {
            if (user == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            Sale sale = _sales.GetById(saleId);
            if (sale == null) throw new ValidationException("الفاتورة غير موجودة.");
            if (sale.Status == SaleStatus.Returned) throw new ValidationException("الفاتورة مُرجعة بالفعل.");
            return _sales.ReturnItems(saleId, user.Id, reason, requests);
        }

        public Sale GetSale(int id) => _sales.GetById(id);

        /// <summary>
        /// Finds a sale by the invoice number typed into the return screen. That number is a plain
        /// integer; receipts printed before V1.8 carry the old composite form, which
        /// <see cref="SaleNumberParser"/> still resolves. Returns null when nothing matches.
        /// </summary>
        public Sale FindSale(string query)
        {
            foreach (int number in SaleNumberParser.Candidates(query))
            {
                Sale found = _sales.GetBySaleNumber(number);
                if (found != null) return found;
            }
            return null;
        }

        public IReadOnlyList<Sale> SalesOn(DateTime day)
            => _sales.GetByDateRange(day.Date, day.Date.AddDays(1));

        public IReadOnlyList<Sale> SalesInRange(DateTime fromInclusive, DateTime toExclusive)
            => _sales.GetByDateRange(fromInclusive, toExclusive);

        /// <summary>
        /// Returns the nearest expiry date for an item if it falls within the POS warning window
        /// (FR-POS-09), otherwise null. The POS warns but does not block.
        /// </summary>
        public DateTime? NearExpiryWarning(int itemId)
        {
            int window = int.TryParse(_settings.Get("pos_expiry_warn_days"), out int w) ? w : 30;
            // Spent batches are excluded: an empty box that expired last year is not a reason to warn
            // the cashier about the stock they are actually selling.
            var batches = _stock.GetSellableBatches(itemId);
            DateTime? nearest = ExpiryEvaluator.NearestExpiry(batches);
            if (nearest == null) return null;
            return (nearest.Value.Date - DateTime.Now.Date).Days <= window ? nearest : null;
        }
    }
}
