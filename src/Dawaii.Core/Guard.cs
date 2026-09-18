using Dawaii.Core.Models;

namespace Dawaii.Core
{
    /// <summary>Shared permission checks (SRS 2.1 / NFR-04) so every service enforces roles the same way.</summary>
    public static class Guard
    {
        /// <summary>Throws <see cref="PermissionDeniedException"/> unless the user is an active Admin.</summary>
        public static void RequireAdmin(User user, string message)
        {
            if (user == null || !user.IsAdmin)
                throw new PermissionDeniedException(message);
        }

        /// <summary>Throws <see cref="PermissionDeniedException"/> unless someone is signed in and active.</summary>
        public static void RequireUser(User user, string message)
        {
            if (user == null || !user.IsActive)
                throw new PermissionDeniedException(message);
        }

        /// <summary>Throws unless the user may work in the catalog/stockroom — the manager, or an
        /// employee the manager promoted to "موظف ذو امتيازات". A plain cashier is refused.</summary>
        public static void RequireInventoryAccess(User user, string message)
        {
            if (user == null || !user.IsActive || !user.CanManageInventory)
                throw new PermissionDeniedException(message);
        }

        /// <summary>Throws unless the user may work on the buying side — companies, orders, and opening
        /// a new drug that arrives on one. The manager or a "موظف ذو امتيازات" (V2.2).</summary>
        public static void RequirePurchasingAccess(User user, string message)
        {
            if (user == null || !user.IsActive || !user.CanManagePurchasing)
                throw new PermissionDeniedException(message);
        }
    }
}
