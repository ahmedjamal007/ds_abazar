using System;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Security;

namespace Dawaii.Core.Services
{
    public class AuthResult
    {
        public bool Success { get; private set; }
        public User User { get; private set; }
        public string Message { get; private set; }

        public static AuthResult Ok(User user) => new AuthResult { Success = true, User = user };
        public static AuthResult Fail(string message) => new AuthResult { Success = false, Message = message };
    }

    /// <summary>Login (FR-USR-01). Verifies credentials against hashed passwords (NFR-04).</summary>
    public class AuthService
    {
        private readonly IUserRepository _users;

        public AuthService(IUserRepository users)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
        }

        public AuthResult Authenticate(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return AuthResult.Fail("الرجاء إدخال اسم المستخدم وكلمة المرور.");

            User user = _users.GetByUsername(username.Trim());
            if (user == null || !user.IsActive)
                return AuthResult.Fail("اسم المستخدم أو كلمة المرور غير صحيحة.");

            if (!PasswordHasher.Verify(password, user.PasswordHash))
                return AuthResult.Fail("اسم المستخدم أو كلمة المرور غير صحيحة.");

            return AuthResult.Ok(user);
        }
    }
}
