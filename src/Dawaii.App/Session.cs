using Dawaii.Core.Models;

namespace Dawaii.App
{
    /// <summary>Holds the process-wide services and the currently logged-in user.</summary>
    public static class Session
    {
        public static AppServices Services { get; set; }
        public static User CurrentUser { get; set; }

        public static bool IsAdmin => CurrentUser != null && CurrentUser.IsAdmin;

        /// <summary>Manager or "موظف ذو امتيازات" — the screens that change the catalog/stock ask this.</summary>
        public static bool CanManageInventory => CurrentUser != null && CurrentUser.CanManageInventory;

        /// <summary>Manager or "موظف ذو امتيازات" — the companies and orders screens ask this.</summary>
        public static bool CanManagePurchasing => CurrentUser != null && CurrentUser.CanManagePurchasing;

        /// <summary>Manager or "موظف ذو امتيازات" — the customers and debt ledger screen asks this.</summary>
        public static bool CanManageCustomers => CurrentUser != null && CurrentUser.CanManageCustomers;

        /// <summary>
        /// Re-reads the signed-in user from the database and reports whether the session may continue
        /// (V2.3).
        ///
        /// <see cref="CurrentUser"/> is a snapshot taken at login, and every permission check in the
        /// application reads it. Nothing re-read it, so a manager who deactivated an employee — or
        /// demoted them out of "موظف ذو امتيازات" — changed nothing on the counter that employee was
        /// standing at until they chose to log out, which on a pharmacy PC can be days. The manager's
        /// only real remedy was to walk over and close the program.
        ///
        /// Returns false when the account is gone or deactivated; the caller signs out. A database that
        /// cannot be reached returns true and keeps the session, because a dropped network in the middle
        /// of a shift must not throw the pharmacy out of the till.
        /// </summary>
        public static bool Refresh()
        {
            if (CurrentUser == null) return false;
            try
            {
                User stored = Services?.Users?.GetById(CurrentUser.Id);
                if (stored == null || !stored.IsActive) return false;
                CurrentUser = stored;
                return true;
            }
            catch { return true; }
        }

        public static void SignOut() => CurrentUser = null;
    }
}
