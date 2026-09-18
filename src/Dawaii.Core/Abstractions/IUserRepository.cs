using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    public interface IUserRepository
    {
        User GetByUsername(string username);
        User GetById(int id);
        IReadOnlyList<User> GetAll();
        int Count();
        int Add(User user);

        /// <summary>Saves the account's identity and role: username, full name, role, active flag.</summary>
        void Update(User user);

        /// <summary>
        /// Removes the account together with its HR records (attendance, profile, leaves, deductions).
        /// Returns false — changing nothing — when the employee has financial or stock history that must
        /// stay attributable to them; the caller should deactivate the account instead.
        /// </summary>
        bool Delete(int userId);

        void SetActive(int userId, bool active);
        void UpdatePasswordHash(int userId, string passwordHash);
    }
}
