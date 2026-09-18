using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Purchases (V1.4 "المشتريات"): money the pharmacy pays to people who come to sell goods —
    /// bags (أكياس) and other supplies. Any signed-in user may record a purchase (an employee buys
    /// something for the shop); removing an entry is Admin-only. Standalone log — no stock impact.
    /// </summary>
    public class PurchaseService
    {
        private readonly IPurchaseRepository _purchases;
        private readonly IAuditRepository _audit;

        public PurchaseService(IPurchaseRepository purchases, IAuditRepository audit)
        {
            _purchases = purchases;
            _audit = audit;
        }

        /// <summary>Records a purchase. Total = quantity × unit price (rounded to 2dp).</summary>
        public int AddPurchase(User actor, string description, int quantity, decimal unitPrice,
            string supplierName = null, string note = null)
        {
            if (actor == null) throw new PermissionDeniedException("يجب تسجيل الدخول لتسجيل مشترى.");
            if (string.IsNullOrWhiteSpace(description)) throw new ValidationException("بيان المشترى مطلوب.");
            if (quantity <= 0) throw new ValidationException("الكمية يجب أن تكون أكبر من صفر.");
            if (unitPrice < 0) throw new ValidationException("السعر غير صالح.");

            decimal amount = decimal.Round(unitPrice * quantity, 2);
            var purchase = new Purchase
            {
                SupplierName = string.IsNullOrWhiteSpace(supplierName) ? null : supplierName.Trim(),
                Description = description.Trim(),
                Quantity = quantity,
                UnitPrice = decimal.Round(unitPrice, 2),
                Amount = amount,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                UserId = actor.Id,
                CreatedAt = DateTime.Now
            };
            int id = _purchases.Add(purchase);
            _audit.Log(actor, "AddPurchase", "purchases", id, $"{purchase.Description} x{quantity} = {amount:0.00}");
            return id;
        }

        public IReadOnlyList<Purchase> InRange(DateTime fromInclusive, DateTime toExclusive)
            => _purchases.GetRange(fromInclusive, toExclusive);

        public decimal TotalInRange(DateTime fromInclusive, DateTime toExclusive)
            => _purchases.TotalInRange(fromInclusive, toExclusive);

        public void DeletePurchase(User admin, int id)
        {
            Guard.RequireAdmin(admin, "حذف المشتريات متاح للمدير فقط.");
            _purchases.Delete(id);
            _audit.Log(admin, "DeletePurchase", "purchases", id, null);
        }
    }
}
