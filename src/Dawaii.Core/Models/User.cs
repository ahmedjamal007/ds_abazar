using System;

namespace Dawaii.Core.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string FullName { get; set; }
        public Role Role { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }

        public bool IsAdmin => Role == Role.Admin;

        /// <summary>
        /// The catalog and the stockroom — adding, editing and deleting drugs, receiving batches by
        /// hand, barcodes, disposals. The manager and the "موظف ذو امتيازات".
        ///
        /// V2.2 briefly narrowed this to the manager alone, on the reasoning that the privileged
        /// employee's job had moved to the buying side. It had not: the same person files the order and
        /// unpacks the boxes onto the shelf, so removing the stockroom only meant fetching the manager
        /// to correct a quantity. V2.3 restores it — the role covers both sides of the stockroom now,
        /// what is ordered and what is on the shelf.
        /// </summary>
        public bool CanManageInventory => Role == Role.Admin || Role == Role.FullEmployee;

        /// <summary>
        /// The buying side — companies, their orders, what is still owed on them, and paying it.
        /// Every member of staff (V2.4).
        ///
        /// It was the manager and the "موظف ذو امتيازات". Deliveries do not wait for either: the
        /// distributor's driver is at the door now, and whoever is standing there has to be able to
        /// take the boxes and type the invoice. Making that wait for the right person to be free is
        /// how a delivery ends up entered from memory hours later, or not at all.
        ///
        /// Kept as a right of its own rather than folded back into <see cref="CanManageInventory"/> —
        /// they now cover different people, and what a supplier is owed was never the same kind of
        /// fact as a shelf quantity. What stays out of a plain cashier's reach: deleting a company and
        /// the purchase report (both the manager's), and the catalogue and stockroom, which remain
        /// behind <see cref="CanManageInventory"/>. The one crossing point is opening a drug that
        /// arrives on a delivery — see InventoryService.CreateItemForDelivery.
        /// </summary>
        public bool CanManagePurchasing
            => Role == Role.Admin || Role == Role.FullEmployee || Role == Role.Cashier;

        /// <summary>True for the manager and for "موظف ذو امتيازات" — the customers and debt ledger
        /// screen. The privileged employee looks after the accounts at the counter: they may open the
        /// ledger, add a customer and take a repayment. Correcting or deleting an account holder, and
        /// adding a debt by hand, stay the manager's (the service refuses anyone else).</summary>
        public bool CanManageCustomers => Role == Role.Admin || Role == Role.FullEmployee;
    }
}
