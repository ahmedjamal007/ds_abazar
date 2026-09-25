using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class AuthAndUserTests
    {
        private FakeUserRepository _users;
        private AuthService _auth;
        private UserService _userService;
        private User _admin;

        [SetUp]
        public void SetUp()
        {
            _users = new FakeUserRepository();
            _auth = new AuthService(_users);
            _userService = new UserService(_users);

            _admin = new User
            {
                Username = "admin",
                PasswordHash = Dawaii.Core.Security.PasswordHasher.Hash("admin123"),
                Role = Role.Admin,
                IsActive = true
            };
            _admin.Id = _users.Add(_admin);
        }

        [Test]
        public void Authenticate_ValidCredentials_Succeeds()
        {
            AuthResult result = _auth.Authenticate("admin", "admin123");
            Assert.That(result.Success, Is.True);
            Assert.That(result.User.Role, Is.EqualTo(Role.Admin));
        }

        [Test]
        public void Authenticate_WrongPassword_Fails()
        {
            Assert.That(_auth.Authenticate("admin", "nope").Success, Is.False);
        }

        [Test]
        public void Authenticate_InactiveUser_Fails()
        {
            _users.SetActive(_admin.Id, false);
            Assert.That(_auth.Authenticate("admin", "admin123").Success, Is.False);
        }

        [Test]
        public void CreateUser_ByAdmin_HashesPasswordAndPersists()
        {
            int id = _userService.CreateUser(_admin, "sara", "cashierpw", "سارة", Role.Cashier);
            User created = _users.GetById(id);

            Assert.That(created, Is.Not.Null);
            Assert.That(created.Role, Is.EqualTo(Role.Cashier));
            Assert.That(created.PasswordHash, Does.Not.Contain("cashierpw"), "Password must be hashed.");
            Assert.That(_auth.Authenticate("sara", "cashierpw").Success, Is.True);
        }

        [Test]
        public void CreateUser_ByCashier_IsDenied()
        {
            var cashier = new User { Username = "c", Role = Role.Cashier, IsActive = true };
            cashier.Id = _users.Add(cashier);
            Assert.Throws<PermissionDeniedException>(
                () => _userService.CreateUser(cashier, "x", "pwpw", "x", Role.Cashier));
        }

        [Test]
        public void CreateUser_DuplicateUsername_IsRejected()
        {
            Assert.Throws<ValidationException>(
                () => _userService.CreateUser(_admin, "admin", "another", "dup", Role.Cashier));
        }

        [Test]
        public void CreateUser_AsPrivilegedEmployee_GetsTheStockroomAndTheBuyingSide()
        {
            int id = _userService.CreateUser(_admin, "omar", "omarpw", "عمر", Role.FullEmployee);
            User created = _users.GetById(id);

            Assert.That(created.Role, Is.EqualTo(Role.FullEmployee));
            Assert.That(created.CanManageInventory, Is.True, "the stockroom is open to them");
            Assert.That(created.CanManagePurchasing, Is.True, "and so are the companies and their orders");
            Assert.That(created.IsAdmin, Is.False, "but they are not a manager — money and staff stay closed");
        }

        [Test]
        public void SetRole_PromotesAndDemotesAnExistingEmployee()
        {
            int id = _userService.CreateUser(_admin, "sara", "cashierpw", "سارة", Role.Cashier);

            // Read through CanManageInventory, not CanManagePurchasing: buying belongs to every member
            // of staff since V2.4, so it no longer tells the two roles apart. The stockroom does.
            _userService.SetRole(_admin, id, Role.FullEmployee);
            Assert.That(_users.GetById(id).CanManageInventory, Is.True);

            _userService.SetRole(_admin, id, Role.Cashier);
            Assert.That(_users.GetById(id).CanManageInventory, Is.False, "access can be withdrawn again");
        }

        [Test]
        public void SetRole_ByCashier_IsDenied_AndAdminCannotDemoteSelf()
        {
            int id = _userService.CreateUser(_admin, "sara", "cashierpw", "سارة", Role.Cashier);
            User sara = _users.GetById(id);

            Assert.Throws<PermissionDeniedException>(() => _userService.SetRole(sara, id, Role.FullEmployee));
            Assert.Throws<ValidationException>(() => _userService.SetRole(_admin, _admin.Id, Role.Cashier));
        }

        [Test]
        public void SetActive_AdminCannotDisableSelf()
        {
            Assert.Throws<ValidationException>(() => _userService.SetActive(_admin, _admin.Id, false));
        }

        [Test]
        public void ChangeOwnPassword_WrongCurrent_Throws()
        {
            Assert.Throws<ValidationException>(
                () => _userService.ChangeOwnPassword(_admin, "wrong", "brandnew"));
        }

        [Test]
        public void ChangeOwnPassword_Valid_UpdatesHash()
        {
            _userService.ChangeOwnPassword(_admin, "admin123", "brandnew");
            Assert.That(_auth.Authenticate("admin", "brandnew").Success, Is.True);
            Assert.That(_auth.Authenticate("admin", "admin123").Success, Is.False);
        }

        // ---------------- V1.9: renaming and deleting employees ----------------

        [Test]
        public void Rename_ChangesLoginNameAndDisplayName()
        {
            int id = _userService.CreateUser(_admin, "fawz", "pass1234", "فواز", Role.Cashier);
            _userService.Rename(_admin, id, "fawaz", "فواز فخرالدين");

            User renamed = _users.GetById(id);
            Assert.That(renamed.Username, Is.EqualTo("fawaz"));
            Assert.That(renamed.FullName, Is.EqualTo("فواز فخرالدين"));
            Assert.That(_auth.Authenticate("fawaz", "pass1234").Success, Is.True, "signs in under the new name");
            Assert.That(_auth.Authenticate("fawz", "pass1234").Success, Is.False, "and not the old one");
        }

        [Test]
        public void Rename_ToAnotherEmployeesLoginName_Throws()
        {
            int sara = _userService.CreateUser(_admin, "sara", "pass1234", "سارة", Role.Cashier);
            _userService.CreateUser(_admin, "yousif", "pass1234", "يوسف", Role.Cashier);

            Assert.Throws<ValidationException>(() => _userService.Rename(_admin, sara, "yousif", "سارة"));
        }

        [Test]
        public void Rename_KeepingOwnUsername_IsAllowed()
        {
            // Fixing only the display name must not trip the "already taken" check on their own name.
            int id = _userService.CreateUser(_admin, "sara", "pass1234", "سارة", Role.Cashier);
            Assert.DoesNotThrow(() => _userService.Rename(_admin, id, "sara", "سارة علي"));
            Assert.That(_users.GetById(id).FullName, Is.EqualTo("سارة علي"));
        }

        [Test]
        public void Rename_ByNonAdmin_Denied()
        {
            int id = _userService.CreateUser(_admin, "sara", "pass1234", "سارة", Role.Cashier);
            var cashier = new User { Id = id, Role = Role.Cashier, IsActive = true };
            Assert.Throws<PermissionDeniedException>(() => _userService.Rename(cashier, id, "sara2", "سارة"));
        }

        [Test]
        public void DeleteUser_RemovesAnEmployeeWithNoHistory()
        {
            int id = _userService.CreateUser(_admin, "temp", "pass1234", "مؤقت", Role.Cashier);
            Assert.That(_userService.DeleteUser(_admin, id), Is.True);
            Assert.That(_users.GetById(id), Is.Null);
        }

        [Test]
        public void DeleteUser_WithSalesHistory_IsRefusedSoItCanBeDeactivatedInstead()
        {
            int id = _userService.CreateUser(_admin, "sara", "pass1234", "سارة", Role.Cashier);
            _users.UsersWithHistory.Add(id);

            Assert.That(_userService.DeleteUser(_admin, id), Is.False);
            Assert.That(_users.GetById(id), Is.Not.Null, "the account stays, so its sales stay attributable");
        }

        [Test]
        public void DeleteUser_CannotDeleteSelf_NorTheLastAdmin()
        {
            Assert.Throws<ValidationException>(() => _userService.DeleteUser(_admin, _admin.Id));

            // A second admin, deleted by the first: the pharmacy still has one, so this is allowed.
            int other = _userService.CreateUser(_admin, "admin2", "pass1234", "مدير ٢", Role.Admin);
            Assert.That(_userService.DeleteUser(_admin, other), Is.True);
        }

        [Test]
        public void DeleteUser_LastRemainingAdmin_Throws()
        {
            // The acting admin is deactivated here only so they are not counted as the survivor.
            int other = _userService.CreateUser(_admin, "admin2", "pass1234", "مدير ٢", Role.Admin);
            _users.SetActive(_admin.Id, false);

            Assert.Throws<ValidationException>(() => _userService.DeleteUser(_admin, other));
        }
    }
}
