using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Who may take a delivery, and what that does NOT let them do (V2.4).
    ///
    /// Buying moved to every member of staff. A distributor's driver does not wait for the right
    /// person to be free, and a delivery that has to sit until one is gets typed from memory hours
    /// later or not at all.
    ///
    /// The point of these tests is the boundary, not the grant. Opening the buying side to a plain
    /// cashier must not quietly hand them the stockroom through the invoice screen's item picker —
    /// which is exactly the door that exists, because a delivery may carry a drug the pharmacy has
    /// never stocked. So there is one crossing point, CreateItemForDelivery, and everything either
    /// side of it is pinned here: a cashier can open a drug that arrived, and can do nothing else to
    /// the catalogue or the shelf.
    /// </summary>
    [TestFixture]
    public class PurchasingAccessTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private SqliteItemRepository _items;
        private SqliteAuditRepository _audit;
        private InventoryService _inventory;
        private SupplierService _supplierService;
        private User _admin, _privileged, _cashier;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_perm_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();
            init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _audit = new SqliteAuditRepository(_db);
            var stock = new SqliteStockRepository(_db);

            _inventory = new InventoryService(_items, stock, new SqliteSettingsRepository(_db), _audit,
                new SqliteItemCodeRepository(_db));
            _supplierService = new SupplierService(new SqliteSupplierRepository(_db), _items, _audit);

            int adminId = new SqliteUserRepository(_db).GetByUsername("admin").Id;
            _admin = new User { Id = adminId, Username = "admin", Role = Role.Admin, IsActive = true };
            _privileged = new User { Id = adminId, Username = "priv", Role = Role.FullEmployee, IsActive = true };
            _cashier = new User { Id = adminId, Username = "sara", Role = Role.Cashier, IsActive = true };
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static Item Drug(string name = "zz-new-drug")
            => new Item { NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10, IsActive = true };

        // ---------------- who may buy ----------------

        [Test]
        public void EveryMemberOfStaff_MayWorkTheBuyingSide()
        {
            Assert.That(_admin.CanManagePurchasing, Is.True);
            Assert.That(_privileged.CanManagePurchasing, Is.True);
            Assert.That(_cashier.CanManagePurchasing, Is.True,
                "the driver is at the door now — whoever is standing there has to be able to take it");
        }

        [Test]
        public void ADeactivatedAccount_MayNot_WhateverItsRole()
        {
            var sacked = new User { Id = 9, Username = "gone", Role = Role.Cashier, IsActive = false };

            Assert.Throws<PermissionDeniedException>(
                () => _supplierService.CreateSupplier(sacked, "شركة النيل"));
        }

        [Test]
        public void ACashier_MayOpenACompanyAndFileItsDelivery()
        {
            int supplierId = _supplierService.CreateSupplier(_cashier, "شركة النيل");
            int drug = _inventory.CreateItem(_admin, Drug());

            PurchaseInvoice invoice = _supplierService.RecordInvoice(
                _cashier, supplierId, "المندوب علي", "INV-1", DateTime.Today,
                new[]
                {
                    new PurchaseInvoiceLine
                    {
                        ItemId = drug, QuantityBoxes = 2, StripsPerBox = 10,
                        BoxPurchasePrice = 1000m, BoxSellingPrice = 1400m,
                        ExpiryDate = DateTime.Today.AddYears(1), BatchNumber = "LOT-1"
                    }
                });

            Assert.That(invoice.Total, Is.EqualTo(2000m));
        }

        // ---------------- the one crossing point ----------------

        [Test]
        public void ACashier_MayOpenADrugThatArrivedOnADelivery()
        {
            int id = _inventory.CreateItemForDelivery(_cashier, Drug());

            Assert.That(_items.GetById(id), Is.Not.Null,
                "a delivery routinely carries something never stocked before; being stuck here " +
                "means boxes on the counter and a manager who is not free");
        }

        [Test]
        public void OpeningADrugOnADelivery_IsAuditedApartFromTheCatalogueScreen()
        {
            _inventory.CreateItemForDelivery(_cashier, Drug("zz-from-delivery"));
            _inventory.CreateItem(_admin, Drug("zz-from-catalogue"));

            var actions = _audit.GetRecent(50).Select(e => e.Action).ToList();

            Assert.That(actions, Does.Contain("CreateItemOnDelivery"));
            Assert.That(actions, Does.Contain("CreateItem"));
            Assert.That(actions, Is.Unique,
                "a manager reading the log can tell a drug opened at the counter mid-delivery " +
                "from one added deliberately to the catalogue");
        }

        [Test]
        public void ADrugOpenedOnADelivery_IsStillValidated()
        {
            // The relaxed right is about WHO, not about what a drug is allowed to be.
            Assert.Throws<ValidationException>(
                () => _inventory.CreateItemForDelivery(_cashier, new Item { NameEn = "", IsActive = true }));
        }

        [Test]
        public void SomeoneWithNoBuyingRightAtAll_CannotUseTheDeliveryDoor()
        {
            var sacked = new User { Id = 9, Username = "gone", Role = Role.Cashier, IsActive = false };

            Assert.Throws<PermissionDeniedException>(
                () => _inventory.CreateItemForDelivery(sacked, Drug()));
            Assert.Throws<PermissionDeniedException>(
                () => _inventory.CreateItemForDelivery(null, Drug()));
        }

        // ---------------- and the stockroom stays shut ----------------

        [Test]
        public void ACashier_StillCannotAddADrugThroughTheCatalogue()
        {
            Assert.That(_cashier.CanManageInventory, Is.False);
            Assert.Throws<PermissionDeniedException>(() => _inventory.CreateItem(_cashier, Drug()));
        }

        [Test]
        public void ACashier_CannotRenameOrRepriceWhatIsAlreadyOnTheShelf()
        {
            int drug = _inventory.CreateItem(_admin, Drug("zz-panadol"));
            Item stored = _items.GetById(drug);
            stored.NameEn = "zz-renamed";

            Assert.Throws<PermissionDeniedException>(() => _inventory.UpdateItemDetails(_cashier, stored));
        }

        [Test]
        public void ACashier_CannotPutStockOnTheShelfByHand()
        {
            int drug = _inventory.CreateItemForDelivery(_cashier, Drug());

            // Opening the drug is the cashier's; putting boxes on the shelf outside a delivery is not.
            Assert.Throws<PermissionDeniedException>(
                () => _inventory.ReceiveStock(_cashier, drug, 10, DateTime.Today.AddYears(1),
                                              1000m, 1400m, 10));
        }

        [Test]
        public void ThePrivilegedEmployeeAndTheManager_KeepEverythingTheyHad()
        {
            Assert.DoesNotThrow(() => _inventory.CreateItem(_privileged, Drug("zz-a")));
            Assert.DoesNotThrow(() => _inventory.CreateItem(_admin, Drug("zz-b")));
            Assert.DoesNotThrow(() => _inventory.CreateItemForDelivery(_privileged, Drug("zz-c")));
        }

        // ---------------- what stays the manager's ----------------

        [Test]
        public void DeletingACompany_IsStillTheManagersAlone()
        {
            int supplierId = _supplierService.CreateSupplier(_cashier, "شركة النيل");

            Assert.Throws<PermissionDeniedException>(
                () => _supplierService.DeleteSupplier(_cashier, supplierId));
            Assert.Throws<PermissionDeniedException>(
                () => _supplierService.DeleteSupplier(_privileged, supplierId));
        }

        [Test]
        public void ThePurchaseReport_IsStillTheManagersAlone()
        {
            Assert.Throws<PermissionDeniedException>(
                () => _supplierService.OrdersInRange(_cashier, DateTime.Today, DateTime.Today.AddDays(1)));
        }
    }
}
