using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Security;

namespace Dawaii.Core.Services
{
    /// <summary>User administration (Admin only) — create, list, deactivate, reset password (FR-USR-02).</summary>
    public class UserService
    {
        private readonly IUserRepository _users;

        public UserService(IUserRepository users)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
        }

        public IReadOnlyList<User> ListUsers(User actingUser)
        {
            RequireAdmin(actingUser);
            return _users.GetAll();
        }

        public int CreateUser(User actingUser, string username, string password, string fullName, Role role)
        {
            RequireAdmin(actingUser);
            username = (username ?? string.Empty).Trim();

            if (username.Length < 2)
                throw new ValidationException("اسم المستخدم قصير جداً.");
            if (string.IsNullOrEmpty(password) || password.Length < 4)
                throw new ValidationException("كلمة المرور يجب أن تكون 4 أحرف على الأقل.");
            if (_users.GetByUsername(username) != null)
                throw new ValidationException("اسم المستخدم موجود بالفعل.");

            var user = new User
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                FullName = string.IsNullOrWhiteSpace(fullName) ? username : fullName.Trim(),
                Role = role,
                IsActive = true
            };
            return _users.Add(user);
        }

        /// <summary>Changes an employee's role — how the manager grants or withdraws stockroom access
        /// ("موظف ذو امتيازات") for an account that already exists. The manager cannot demote themselves,
        /// which would leave the pharmacy with no one able to manage users.</summary>
        public void SetRole(User actingUser, int userId, Role role)
        {
            RequireAdmin(actingUser);
            if (actingUser.Id == userId && role != Role.Admin)
                throw new ValidationException("لا يمكنك تغيير صلاحية حسابك الحالي.");

            User user = _users.GetById(userId);
            if (user == null) throw new ValidationException("المستخدم غير موجود.");
            user.Role = role;
            _users.Update(user);
        }

        /// <summary>
        /// Renames an employee: their display name, and the username they sign in with. The login name
        /// has to stay unique, and stays theirs — comparing against the account that already holds it
        /// lets someone re-save their own name unchanged (or just fix its capitalisation).
        /// </summary>
        public void Rename(User actingUser, int userId, string username, string fullName)
        {
            RequireAdmin(actingUser);
            username = (username ?? string.Empty).Trim();
            if (username.Length < 2) throw new ValidationException("اسم المستخدم قصير جداً.");

            User user = _users.GetById(userId);
            if (user == null) throw new ValidationException("المستخدم غير موجود.");

            User owner = _users.GetByUsername(username);
            if (owner != null && owner.Id != userId)
                throw new ValidationException("اسم المستخدم موجود بالفعل.");

            user.Username = username;
            user.FullName = string.IsNullOrWhiteSpace(fullName) ? username : fullName.Trim();
            _users.Update(user);
        }

        /// <summary>
        /// Deletes an employee's account. Returns false when they have sales, refunds, stock or ledger
        /// history — that stays attributable, so the caller should offer to deactivate instead.
        /// The manager cannot delete their own account, nor the last admin left: either would leave the
        /// pharmacy with nobody able to manage users or prices.
        /// </summary>
        public bool DeleteUser(User actingUser, int userId)
        {
            RequireAdmin(actingUser);
            if (actingUser.Id == userId) throw new ValidationException("لا يمكنك حذف حسابك الحالي.");

            User user = _users.GetById(userId);
            if (user == null) throw new ValidationException("المستخدم غير موجود.");
            if (user.Role == Role.Admin && CountOtherActiveAdmins(userId) == 0)
                throw new ValidationException("لا يمكن حذف آخر حساب مدير في النظام.");

            return _users.Delete(userId);
        }

        private int CountOtherActiveAdmins(int excludingUserId)
        {
            int admins = 0;
            foreach (User u in _users.GetAll())
                if (u.Id != excludingUserId && u.Role == Role.Admin && u.IsActive) admins++;
            return admins;
        }

        public void SetActive(User actingUser, int userId, bool active)
        {
            RequireAdmin(actingUser);
            if (actingUser.Id == userId && !active)
                throw new ValidationException("لا يمكنك تعطيل حسابك الحالي.");
            _users.SetActive(userId, active);
        }

        public void ResetPassword(User actingUser, int userId, string newPassword)
        {
            RequireAdmin(actingUser);
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 4)
                throw new ValidationException("كلمة المرور يجب أن تكون 4 أحرف على الأقل.");
            _users.UpdatePasswordHash(userId, PasswordHasher.Hash(newPassword));
        }

        /// <summary>A user may change their own password after re-entering the current one.</summary>
        public void ChangeOwnPassword(User actingUser, string currentPassword, string newPassword)
        {
            if (actingUser == null) throw new ArgumentNullException(nameof(actingUser));
            User fresh = _users.GetById(actingUser.Id);
            if (fresh == null || !PasswordHasher.Verify(currentPassword, fresh.PasswordHash))
                throw new ValidationException("كلمة المرور الحالية غير صحيحة.");
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 4)
                throw new ValidationException("كلمة المرور الجديدة يجب أن تكون 4 أحرف على الأقل.");
            _users.UpdatePasswordHash(actingUser.Id, PasswordHasher.Hash(newPassword));
        }

        private static void RequireAdmin(User actingUser)
            => Guard.RequireAdmin(actingUser, "إدارة المستخدمين متاحة للمدير فقط.");
    }
}
