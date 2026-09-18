using System;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class PurchaseServiceTests
    {
        private FakePurchaseRepository _repo;
        private FakeAuditRepository _audit;
        private PurchaseService _svc;
        private User _admin, _cashier;

        [SetUp]
        public void SetUp()
        {
            _repo = new FakePurchaseRepository();
            _audit = new FakeAuditRepository();
            _svc = new PurchaseService(_repo, _audit);
            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };
            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };
        }

        [Test]
        public void AddPurchase_ComputesTotal_AndStoresEntry()
        {
            int id = _svc.AddPurchase(_cashier, "أكياس", quantity: 200, unitPrice: 3.5m, supplierName: "أحمد");
            Purchase p = _repo.Purchases.Single(x => x.Id == id);
            Assert.That(p.Amount, Is.EqualTo(700m));
            Assert.That(p.Quantity, Is.EqualTo(200));
            Assert.That(p.Description, Is.EqualTo("أكياس"));
            Assert.That(p.SupplierName, Is.EqualTo("أحمد"));
            Assert.That(p.UserId, Is.EqualTo(_cashier.Id));
        }

        [Test]
        public void AddPurchase_AnyLoggedInUserMayRecord()
        {
            Assert.DoesNotThrow(() => _svc.AddPurchase(_cashier, "قفازات", 1, 50m));
            Assert.Throws<PermissionDeniedException>(() => _svc.AddPurchase(null, "قفازات", 1, 50m));
        }

        [Test]
        public void AddPurchase_RejectsInvalidInput()
        {
            Assert.Throws<ValidationException>(() => _svc.AddPurchase(_admin, "  ", 1, 10m), "empty description");
            Assert.Throws<ValidationException>(() => _svc.AddPurchase(_admin, "x", 0, 10m), "zero quantity");
            Assert.Throws<ValidationException>(() => _svc.AddPurchase(_admin, "x", 1, -1m), "negative price");
        }

        [Test]
        public void TotalInRange_SumsOnlyThePeriod()
        {
            _svc.AddPurchase(_admin, "أكياس", 10, 2m);       // 20, today
            _svc.AddPurchase(_admin, "ورق", 5, 4m);          // 20, today
            DateTime today = DateTime.Now.Date;
            Assert.That(_svc.TotalInRange(today, today.AddDays(1)), Is.EqualTo(40m));
            Assert.That(_svc.TotalInRange(today.AddDays(1), today.AddDays(2)), Is.EqualTo(0m));
        }

        [Test]
        public void DeletePurchase_IsAdminOnly()
        {
            int id = _svc.AddPurchase(_cashier, "أكياس", 1, 10m);
            Assert.Throws<PermissionDeniedException>(() => _svc.DeletePurchase(_cashier, id));
            _svc.DeletePurchase(_admin, id);
            Assert.That(_repo.Purchases, Is.Empty);
        }
    }
}
