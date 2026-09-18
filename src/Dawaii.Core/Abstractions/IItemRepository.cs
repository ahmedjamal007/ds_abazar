using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    public interface IItemRepository
    {
        Item GetById(int id);
        IReadOnlyList<Item> GetByIds(IEnumerable<int> ids);

        /// <summary>Incremental search over Arabic + English + generic names (FR-POS-01).</summary>
        IReadOnlyList<Item> Search(string term, bool activeOnly = true, int limit = 50);

        IReadOnlyList<Item> GetAll(bool activeOnly = true);

        int Add(Item item);
        void Update(Item item);
        void SetActive(int itemId, bool active);

        /// <summary>Permanently deletes an item and its stock/codes/history. Returns false (deletes nothing)
        /// if the item is referenced by any sale, so sales history is never orphaned.</summary>
        bool Delete(int itemId);

        /// <summary>Updates only the selling price per single unit.</summary>
        void UpdateSellingPrice(int itemId, decimal newPrice);

        /// <summary>
        /// Sets the item's selling price per single unit AND brings every non-disposed batch of it to
        /// the same price, in one transaction (V2.3 إدارة الأسعار). The two are written together
        /// because the POS reads the item and the batches screen reads the batches, and a failure in
        /// between would have them quoting two different prices for the same box. <paramref name="manual"/>
        /// records whether a person typed this price, so a later bulk multiplier knows to leave it alone.
        /// </summary>
        void ApplySellingPrice(int itemId, decimal sellingPerUnit, bool manual);

        /// <summary>Mirrors the item's per-single-unit purchase and selling prices onto it from its
        /// newest stock batch — both always move together, never independently.</summary>
        void UpdatePurchaseAndSellingPrice(int itemId, decimal purchasePerUnit, decimal sellingPerUnit);
    }
}
