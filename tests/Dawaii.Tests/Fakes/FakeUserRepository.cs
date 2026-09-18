using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Tests.Fakes
{
    /// <summary>In-memory <see cref="IUserRepository"/> for unit tests (no MySQL required).</summary>
    public class FakeUserRepository : IUserRepository
    {
        private readonly List<User> _users = new List<User>();
        private int _nextId = 1;

        public User GetByUsername(string username) =>
            _users.FirstOrDefault(u => u.Username == username);

        public User GetById(int id) => _users.FirstOrDefault(u => u.Id == id);

        public IReadOnlyList<User> GetAll() => _users.OrderBy(u => u.Username).ToList();

        public int Count() => _users.Count;

        public int Add(User user)
        {
            user.Id = _nextId++;
            _users.Add(user);
            return user.Id;
        }

        public void Update(User user)
        {
            var existing = GetById(user.Id);
            if (existing == null) return;
            existing.Username = user.Username;
            existing.FullName = user.FullName;
            existing.Role = user.Role;
            existing.IsActive = user.IsActive;
        }

        /// <summary>Set by a test to stand in for an employee who has sales/stock history, which the
        /// real repository refuses to delete over.</summary>
        public readonly HashSet<int> UsersWithHistory = new HashSet<int>();

        public bool Delete(int userId)
        {
            if (UsersWithHistory.Contains(userId)) return false;
            return _users.RemoveAll(u => u.Id == userId) > 0;
        }

        public void SetActive(int userId, bool active)
        {
            var u = GetById(userId);
            if (u != null) u.IsActive = active;
        }

        public void UpdatePasswordHash(int userId, string passwordHash)
        {
            var u = GetById(userId);
            if (u != null) u.PasswordHash = passwordHash;
        }
    }
}
