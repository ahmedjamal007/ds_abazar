using System;
using System.Collections.Generic;
using System.Data.Common;
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
    /// Buying stock from a company and owing them for it (V2.1), against a real database.
    ///
    /// Two things have to hold for this feature to be trustworthy, and everything else follows from
    /// them: what an invoice says arrived is exactly what lands on the shelf, and what it says is owed
    /// is exactly what the totals add up to. The tests are written around those two rather than around
    /// the screens, because both are what a pharmacist would check by hand at the end of a month.
    /// </summary>
    [TestFixture]
    public class SupplierInvoiceTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private SqliteItemRepository _items;
        private SqliteStockRepository _stock;
        private SqliteSupplierRepository _supplierRepo;
        private SupplierService _suppliers;
        private User _admin, _cashier, _privileged;
        private int _supplierId;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_sup_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();
            init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _stock = new SqliteStockRepository(_db);
            _supplierRepo = new SqliteSupplierRepository(_db);
            var users = new SqliteUserRepository(_db);
            _suppliers = new SupplierService(_supplierRepo, _items, new SqliteAuditRepository(_db));

            _admin = users.GetByUsername("admin");
            _cashier = new User { Id = _admin.Id, Username = "cashier", Role = Role.Cashier, IsActive = true };
            _privileged = new User { Id = _admin.Id, Username = "priv", Role = Role.FullEmployee, IsActive = true };

            _supplierId = _suppliers.CreateSupplier(_admin, "شركة النيل");
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* file lock — harmless */ }
        }

        private int NewItem(string name, int unitsPerStrip = 10, int stripsPerBox = 10)
            => _items.Add(new Item
            {
                NameEn = name, GenericName = "generic-" + name,
                UnitsPerStrip = unitsPerStrip, StripsPerBox = stripsPerBox, IsActive = true
            });

        private static PurchaseInvoiceLine Line(int itemId, int boxes, decimal buy, decimal sell, int stripsPerBox = 10)
            => new PurchaseInvoiceLine
            {
                ItemId = itemId, QuantityBoxes = boxes, StripsPerBox = stripsPerBox,
                BoxPurchasePrice = buy, BoxSellingPrice = sell,
                ExpiryDate = DateTime.Today.AddYears(1), BatchNumber = "LOT-1"
            };

        private PurchaseInvoice Record(decimal paidNow, params PurchaseInvoiceLine[] lines)
            => _suppliers.RecordInvoice(_admin, _supplierId, "المندوب علي", "INV-100",
                DateTime.Today, lines, paidNow);

        // ---------------- what arrives on the shelf ----------------

        [Test]
        public void Invoice_PutsEveryLineOnTheShelfAsItsOwnBatch()
        {
            int panadol = NewItem("panadol");
            int brufen = NewItem("brufen");

            Record(0m, Line(panadol, 3, 1000m, 1400m), Line(brufen, 2, 500m, 800m));

            // 3 boxes x 10 strips x 10 units = 300 single units; 2 x 10 x 10 = 200.
            Assert.That(_stock.GetBatches(panadol).Single().QuantityUnits, Is.EqualTo(300));
            Assert.That(_stock.GetBatches(brufen).Single().QuantityUnits, Is.EqualTo(200));
        }

        [Test]
        public void Invoice_ForADrugAlreadyStocked_AddsABatchAndLeavesTheItemAlone()
        {
            int panadol = NewItem("panadol");
            Record(0m, Line(panadol, 2, 1000m, 1400m));
            Item before = _items.GetById(panadol);

            _suppliers.RecordInvoice(_admin, _supplierId, null, "INV-200", DateTime.Today,
                new[] { Line(panadol, 5, 1000m, 1400m) });

            Assert.That(_stock.GetBatches(panadol).Count, Is.EqualTo(2),
                "a second delivery of the same drug is a second batch, not a bigger first one");
            Item after = _items.GetById(panadol);
            Assert.That(after.NameEn, Is.EqualTo(before.NameEn));
            Assert.That(after.GenericName, Is.EqualTo(before.GenericName));
            Assert.That(after.UnitsPerStrip, Is.EqualTo(before.UnitsPerStrip),
                "buying more of a drug must not rewrite what the drug is");
        }

        [Test]
        public void Invoice_PricesTheDrugFromTheNewestDelivery()
        {
            int panadol = NewItem("panadol");
            Record(0m, Line(panadol, 1, 1000m, 2000m));

            // 1 box = 10 strips x 10 units = 100 units, so 2000 a box is 20 a unit.
            Item item = _items.GetById(panadol);
            Assert.That(item.SellingPrice, Is.EqualTo(20m));
            Assert.That(item.PurchasePrice, Is.EqualTo(10m));
        }

        [Test]
        public void Invoice_IsRolledBackWholeWhenALineIsBad()
        {
            int panadol = NewItem("panadol");

            Assert.Throws<ValidationException>(() => Record(0m,
                Line(panadol, 3, 1000m, 1400m),
                Line(999999, 2, 500m, 800m)));      // no such item

            Assert.That(_suppliers.GetInvoices(_supplierId), Is.Empty);
            Assert.That(_stock.GetBatches(panadol), Is.Empty,
                "a delivery that could not be filed must not leave stock behind it");
        }

        // ---------------- what is owed ----------------

        [Test]
        public void Total_IsTheSumOfTheLines_NotSomethingTyped()
        {
            int a = NewItem("a"), b = NewItem("b");
            PurchaseInvoice invoice = Record(0m, Line(a, 3, 1000m, 1400m), Line(b, 2, 250m, 400m));

            Assert.That(invoice.Total, Is.EqualTo(3500m));         // 3x1000 + 2x250
        }

        [Test]
        public void UnpaidInvoice_OwesItsWholeTotal()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = Record(0m, Line(a, 2, 1000m, 1400m));

            Assert.That(invoice.Status, Is.EqualTo(PurchaseInvoiceStatus.Unpaid));
            Assert.That(invoice.Outstanding, Is.EqualTo(2000m));
        }

        [Test]
        public void PaidAtTheDoor_LeavesNothingOutstanding()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = Record(2000m, Line(a, 2, 1000m, 1400m));

            PurchaseInvoice stored = _suppliers.GetInvoice(invoice.Id);
            Assert.That(stored.Status, Is.EqualTo(PurchaseInvoiceStatus.Paid));
            Assert.That(stored.Outstanding, Is.Zero);
            Assert.That(_suppliers.Get(_supplierId).Outstanding, Is.Zero);
        }

        [Test]
        public void PartPayment_LeavesTheRemainderOutstanding()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = Record(0m, Line(a, 2, 1000m, 1400m));

            _suppliers.RecordPayment(_admin, invoice.Id, 750m);

            PurchaseInvoice stored = _suppliers.GetInvoice(invoice.Id);
            Assert.That(stored.Status, Is.EqualTo(PurchaseInvoiceStatus.PartiallyPaid));
            Assert.That(stored.Outstanding, Is.EqualTo(1250m));
        }

        [Test]
        public void Settle_ClearsWhateverIsLeft()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = Record(0m, Line(a, 2, 1000m, 1400m));
            _suppliers.RecordPayment(_admin, invoice.Id, 750m);

            _suppliers.SettleInvoice(_admin, invoice.Id);

            Assert.That(_suppliers.GetInvoice(invoice.Id).Outstanding, Is.Zero);
            Assert.That(_suppliers.GetPayments(invoice.Id).Sum(p => p.Amount), Is.EqualTo(2000m));
        }

        [Test]
        public void Overpaying_IsRefused()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = Record(0m, Line(a, 1, 1000m, 1400m));

            Assert.Throws<ValidationException>(() => _suppliers.RecordPayment(_admin, invoice.Id, 1500m),
                "a supplier handed more than the invoice asks for is a credit to sort out, " +
                "not a negative payable the ledger should absorb");
            Assert.That(_suppliers.GetInvoice(invoice.Id).Outstanding, Is.EqualTo(1000m));
        }

        [Test]
        public void SupplierBalance_IsTheSumOfWhatItsInvoicesStillOwe()
        {
            int a = NewItem("a");
            Record(0m, Line(a, 2, 1000m, 1400m));                                    // owes 2000
            PurchaseInvoice second = _suppliers.RecordInvoice(_admin, _supplierId, null, "INV-2",
                DateTime.Today, new[] { Line(a, 1, 500m, 700m) }, 200m);             // owes 300

            Assert.That(_suppliers.Get(_supplierId).Outstanding, Is.EqualTo(2300m));
            Assert.That(_suppliers.TotalOutstanding(), Is.EqualTo(2300m));
            Assert.That(second.Outstanding, Is.EqualTo(300m));
        }

        [Test]
        public void OnlyCompaniesStillOwedAppearInTheOutstandingList()
        {
            int a = NewItem("a");
            int settledCompany = _suppliers.CreateSupplier(_admin, "شركة مسددة");
            _suppliers.RecordInvoice(_admin, settledCompany, null, "S-1", DateTime.Today,
                new[] { Line(a, 1, 100m, 200m) }, 100m);
            Record(0m, Line(a, 1, 1000m, 1400m));

            var owed = _suppliers.WithOutstanding();

            Assert.That(owed.Select(s => s.Id), Does.Contain(_supplierId));
            Assert.That(owed.Select(s => s.Id), Does.Not.Contain(settledCompany));
        }

        [Test]
        public void InvoiceKeepsTheDrugNameItArrivedUnder()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice invoice = Record(0m, Line(panadol, 1, 1000m, 1400m));

            Item item = _items.GetById(panadol);
            item.NameEn = "panadol-renamed";
            _items.Update(item);

            Assert.That(_suppliers.GetInvoice(invoice.Id).Lines.Single().ItemName,
                Does.StartWith("panadol").And.Not.Contains("renamed"),
                "the invoice records what was delivered on the day — renaming a drug must not rewrite it");
        }

        // ---------------- finding one invoice again ----------------

        [Test]
        public void Search_FindsAnInvoiceByItsNumber()
        {
            int a = NewItem("a");
            _suppliers.RecordInvoice(_admin, _supplierId, "علي", "AX-77", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, "علي", "BX-88", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            var found = _suppliers.SearchInvoices(_supplierId, "AX-77");

            Assert.That(found.Select(i => i.InvoiceNumber), Is.EqualTo(new[] { "AX-77" }));
        }

        [Test]
        public void Search_AlsoMatchesTheRepresentative()
        {
            int a = NewItem("a");
            _suppliers.RecordInvoice(_admin, _supplierId, "المندوب خالد", "N-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, "المندوب سمير", "N-2", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.SearchInvoices(_supplierId, "خالد").Single().InvoiceNumber, Is.EqualTo("N-1"));
        }

        [Test]
        public void Search_IsPartial_SoHalfARememberedNumberIsEnough()
        {
            int a = NewItem("a");
            _suppliers.RecordInvoice(_admin, _supplierId, null, "INV-2026-0042", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.SearchInvoices(_supplierId, "0042").Single().InvoiceNumber, Is.EqualTo("INV-2026-0042"));
        }

        [Test]
        public void Search_ScopedToOneCompany_IgnoresAnotherCompanysMatchingInvoice()
        {
            int a = NewItem("a");
            int other = _suppliers.CreateSupplier(_admin, "شركة أخرى");
            _suppliers.RecordInvoice(_admin, _supplierId, null, "SAME-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, other, null, "SAME-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            var mine = _suppliers.SearchInvoices(_supplierId, "SAME-1");

            Assert.That(mine.Count, Is.EqualTo(1));
            Assert.That(mine.Single().SupplierId, Is.EqualTo(_supplierId));
        }

        [Test]
        public void Search_AcrossEveryCompany_FindsTheNumberWhoeverSentIt()
        {
            int a = NewItem("a");
            int other = _suppliers.CreateSupplier(_admin, "شركة أخرى");
            _suppliers.RecordInvoice(_admin, other, null, "ONLY-THERE", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.SearchInvoices(null, "ONLY-THERE").Single().SupplierName, Is.EqualTo("شركة أخرى"),
                "an invoice turns up and nobody remembers which company sent it — the number alone has to find it");
            Assert.That(_suppliers.SearchInvoices(_supplierId, "ONLY-THERE"), Is.Empty);
        }

        [Test]
        public void Search_AcrossEveryCompany_MatchesTheCompanyName()
        {
            int a = NewItem("a");
            int other = _suppliers.CreateSupplier(_admin, "شركة البركة");
            _suppliers.RecordInvoice(_admin, other, null, "B-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, null, "N-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.SearchInvoices(null, "البركة").Single().InvoiceNumber, Is.EqualTo("B-1"));
        }

        [Test]
        public void Search_WithNoTerm_IsTheCompanysWholeFile()
        {
            int a = NewItem("a");
            _suppliers.RecordInvoice(_admin, _supplierId, null, "N-1", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, null, "N-2", DateTime.Today, new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.SearchInvoices(_supplierId, "").Count, Is.EqualTo(2));
            Assert.That(_suppliers.SearchInvoices(_supplierId, null).Count, Is.EqualTo(2));
        }

        [Test]
        public void ListedInvoice_KnowsHowManyMedicinesAreOnIt()
        {
            int a = NewItem("a"), b = NewItem("b");
            _suppliers.RecordInvoice(_admin, _supplierId, null, "N-1", DateTime.Today,
                new[] { Line(a, 1, 100m, 200m), Line(b, 2, 50m, 90m) });

            // The listing does not load lines, so the count has to come from the query — reading
            // Lines.Count off a listed invoice would report every invoice as empty.
            Assert.That(_suppliers.GetInvoices(_supplierId).Single().LineCount, Is.EqualTo(2));
            Assert.That(_suppliers.SearchInvoices(_supplierId, "N-1").Single().LineCount, Is.EqualTo(2));
        }

        // ---------------- who may do this ----------------

        [Test]
        public void PlainCashier_MayBuyStock_ButNotDeleteACompany()
        {
            // V2.4: buying moved to every member of staff. The driver is at the door now, and a
            // delivery that waits for the right person tends to get typed from memory or not at all.
            int a = NewItem("a");
            Assert.DoesNotThrow(() => _suppliers.CreateSupplier(_cashier, "شركة"));
            Assert.DoesNotThrow(() => _suppliers.RecordInvoice(
                _cashier, _supplierId, null, "X", DateTime.Today, new[] { Line(a, 1, 10m, 20m) }));

            // What did not move: a company is deleted by the manager, and the purchase report is theirs.
            Assert.Throws<PermissionDeniedException>(
                () => _suppliers.DeleteSupplier(_cashier, _supplierId));
            Assert.Throws<PermissionDeniedException>(
                () => _suppliers.OrdersInRange(_cashier, DateTime.Today, DateTime.Today.AddDays(1)));
        }

        [Test]
        public void PrivilegedEmployee_ReachesBothTheStockroomAndTheBuyingSide()
        {
            // V2.2 took the stockroom away and V2.3 gave it back: the person who files the order is the
            // person who unpacks the boxes, so both rights belong to the role.
            Assert.That(_privileged.CanManageInventory, Is.True);
            Assert.That(_privileged.CanManagePurchasing, Is.True);
            Assert.That(_privileged.IsAdmin, Is.False, "but still not a manager — money and staff stay closed");

            // V2.4 opened the buying side to everyone, so this is no longer what separates the roles.
            // The stockroom still is: a plain cashier files deliveries but does not keep the catalogue.
            Assert.That(_cashier.CanManagePurchasing, Is.True);
            Assert.That(_cashier.CanManageInventory, Is.False, "the stockroom is what the promotion buys");
        }

        [Test]
        public void PrivilegedEmployee_MayOpenANewDrug()
        {
            var inventory = new InventoryService(_items, _stock, new SqliteSettingsRepository(_db),
                new SqliteAuditRepository(_db), new SqliteItemCodeRepository(_db));

            int id = inventory.CreateItem(_privileged, new Item
            {
                NameEn = "brand-new", GenericName = "novum", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true
            });

            Assert.That(id, Is.GreaterThan(0),
                "refusing this would stop a delivery halfway through being typed in");
        }

        [Test]
        public void PlainCashier_StillCannotOpenADrug()
        {
            var inventory = new InventoryService(_items, _stock, new SqliteSettingsRepository(_db),
                new SqliteAuditRepository(_db), new SqliteItemCodeRepository(_db));

            Assert.Throws<PermissionDeniedException>(() => inventory.CreateItem(_cashier, new Item
            {
                NameEn = "x", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true
            }));
        }

        /// <summary>
        /// The manager's report has to name the employee who placed each order, and it has to be the
        /// right one — that is the whole point of the column. This uses a real, separately stored user
        /// rather than the fixture's stand-ins, which share the admin's id and so would let the report
        /// print "admin" for everybody and still pass.
        /// </summary>
        [Test]
        public void OrdersReport_NamesTheEmployeeWhoActuallyPlacedEachOrder()
        {
            var users = new SqliteUserRepository(_db);
            int saraId = users.Add(new User
            {
                Username = "sara", PasswordHash = "x", FullName = "سارة المشتريات",
                Role = Role.FullEmployee, IsActive = true
            });
            User sara = users.GetById(saraId);

            int a = NewItem("a");
            _suppliers.RecordInvoice(sara, _supplierId, "المندوب", "BY-SARA", DateTime.Today,
                new[] { Line(a, 2, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, "المندوب", "BY-ADMIN", DateTime.Today,
                new[] { Line(a, 1, 100m, 200m) });

            var report = _suppliers.OrdersInRange(_admin, DateTime.Today, DateTime.Today.AddDays(1));

            Assert.That(report.Single(i => i.InvoiceNumber == "BY-SARA").UserName, Is.EqualTo("سارة المشتريات"),
                "the order sara placed must be attributed to sara, by her full name");
            Assert.That(report.Single(i => i.InvoiceNumber == "BY-ADMIN").UserName, Is.Not.EqualTo("سارة المشتريات"),
                "and the manager's own order must not be attributed to her");

            PurchaseInvoice row = report.Single(i => i.InvoiceNumber == "BY-SARA");
            Assert.That(row.SupplierName, Is.EqualTo("شركة النيل"));
            Assert.That(row.LineCount, Is.EqualTo(1));
            Assert.That(row.CreatedAt.Date, Is.EqualTo(DateTime.Today), "and say when it was entered");
        }

        /// <summary>An employee with no full name recorded is still named — by their username, never
        /// left blank, or the report would have orders nobody is accountable for.</summary>
        [Test]
        public void OrdersReport_FallsBackToTheUsername_WhenNoFullNameIsSet()
        {
            var users = new SqliteUserRepository(_db);
            int id = users.Add(new User
            {
                Username = "omar", PasswordHash = "x", FullName = null,
                Role = Role.FullEmployee, IsActive = true
            });

            int a = NewItem("a");
            _suppliers.RecordInvoice(users.GetById(id), _supplierId, null, "BY-OMAR", DateTime.Today,
                new[] { Line(a, 1, 100m, 200m) });

            Assert.That(_suppliers.OrdersInRange(_admin, DateTime.Today, DateTime.Today.AddDays(1))
                                  .Single(i => i.InvoiceNumber == "BY-OMAR").UserName,
                Is.EqualTo("omar"));
        }

        [Test]
        public void OrdersReport_IsTheManagersAlone()
        {
            Assert.Throws<PermissionDeniedException>(
                () => _suppliers.OrdersInRange(_privileged, DateTime.Today, DateTime.Today.AddDays(1)));
        }

        [Test]
        public void OrdersReport_CoversOnlyThePeriodAsked()
        {
            int a = NewItem("a");
            _suppliers.RecordInvoice(_admin, _supplierId, null, "OLD", DateTime.Today.AddDays(-10),
                new[] { Line(a, 1, 100m, 200m) });
            _suppliers.RecordInvoice(_admin, _supplierId, null, "NEW", DateTime.Today,
                new[] { Line(a, 1, 100m, 200m) });

            var report = _suppliers.OrdersInRange(_admin, DateTime.Today, DateTime.Today.AddDays(1));

            Assert.That(report.Select(i => i.InvoiceNumber), Is.EqualTo(new[] { "NEW" }));
        }

        [Test]
        public void PrivilegedEmployee_MayBuyStock()
        {
            int a = NewItem("a");
            PurchaseInvoice invoice = _suppliers.RecordInvoice(_privileged, _supplierId, null, "P-1",
                DateTime.Today, new[] { Line(a, 1, 10m, 20m) });

            Assert.That(invoice.Id, Is.GreaterThan(0),
                "whoever may receive a shipment may file the invoice it came on");
        }

        [Test]
        public void DeletingACompany_IsRefusedWhileItHasInvoicesOrIsOwedMoney()
        {
            int a = NewItem("a");
            Record(0m, Line(a, 1, 1000m, 1400m));

            Assert.Throws<ValidationException>(() => _suppliers.DeleteSupplier(_admin, _supplierId),
                "a payable is not settled by deleting who it is owed to");

            _suppliers.SettleInvoice(_admin, _suppliers.GetInvoices(_supplierId).Single().Id);
            Assert.That(_suppliers.DeleteSupplier(_admin, _supplierId), Is.False,
                "the invoices stay — they are where the stock on the shelf came from");
        }

        [Test]
        public void AnEmptyInvoice_IsRefused()
        {
            Assert.Throws<ValidationException>(() => _suppliers.RecordInvoice(
                _admin, _supplierId, null, "EMPTY", DateTime.Today, new PurchaseInvoiceLine[0]));
        }

        // ---------------- cash that left the drawer (V2.3) ----------------
        //
        // The shift report reconciles the till. Money handed to a distributor comes out of that same
        // drawer, and until V2.3 supplier_payments was read nowhere outside its own repository — so
        // every cash payment to a company showed up as the cashier being short by that amount.

        [Test]
        public void CashPaidToSuppliers_CountsMoneyHandedOverAtTheDoor()
        {
            int panadol = NewItem("panadol");
            Record(3000m, Line(panadol, 10, 1000m, 1400m));      // paid 3000 cash on delivery

            Assert.That(_suppliers.CashPaidToSuppliers(DateTime.Today, DateTime.Today.AddDays(1)),
                Is.EqualTo(3000m));
        }

        [Test]
        public void CashPaidToSuppliers_CountsLaterPaymentsToo()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            _suppliers.RecordPayment(_admin, filed.Id, 2500m, "دفعة", "Cash");

            Assert.That(_suppliers.CashPaidToSuppliers(DateTime.Today, DateTime.Today.AddDays(1)),
                Is.EqualTo(2500m));
        }

        [Test]
        public void CashPaidToSuppliers_ExcludesBankTransfers()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            _suppliers.RecordPayment(_admin, filed.Id, 4000m, "تحويل", "Bank");
            _suppliers.RecordPayment(_admin, filed.Id, 1000m, "نقداً", "Cash");

            Assert.That(_suppliers.CashPaidToSuppliers(DateTime.Today, DateTime.Today.AddDays(1)),
                Is.EqualTo(1000m),
                "a bank transfer never touches the till, so it must not lower the expected cash");
        }

        [Test]
        public void CashPaidToSuppliers_IgnoresPaymentsOutsideThePeriod()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            _suppliers.RecordPayment(_admin, filed.Id, 1500m, "اليوم", "Cash");

            Assert.That(_suppliers.CashPaidToSuppliers(DateTime.Today.AddDays(-7), DateTime.Today),
                Is.Zero, "yesterday's drawer is not reconciled with today's payments");
        }

        [Test]
        public void SettlingAnInvoice_RecordsHowItWasPaid()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));
            _suppliers.SettleInvoice(_admin, filed.Id, "Bank");

            SupplierPayment payment = _suppliers.GetPayments(filed.Id).Single();
            Assert.That(payment.PaymentMethod, Is.EqualTo("Bank"));
            Assert.That(payment.IsCash, Is.False);
        }

        [Test]
        public void APaymentRecordedBeforeV23_ReadsAsCash()
        {
            // Rows written by an older build have payment_method NULL. The drawer reconciliation has to
            // treat those as cash, because that is how deliveries were actually being settled.
            var legacy = new SupplierPayment { PaymentMethod = null };
            Assert.That(legacy.IsCash, Is.True);
        }

        // ---------------- correcting an invoice that was typed in wrongly (V2.3) ----------------
        //
        // Everything here turns on one thing: the batch row does not hold what was DELIVERED, it holds
        // what is LEFT after the pharmacy has been selling out of it. So a correction has to be applied
        // as a difference. Writing the new quantity straight into the batch is the obvious implementation
        // and it silently restocks every unit sold since the delivery arrived, which is why most of these
        // tests sell something first.

        /// <summary>Re-reads the invoice and hands its lines back as the editor would.</summary>
        private List<PurchaseInvoiceLine> LinesOf(int invoiceId)
            => _suppliers.GetInvoice(invoiceId).Lines.ToList();

        private PurchaseInvoice Edit(int invoiceId, List<PurchaseInvoiceLine> lines,
            string number = "INV-100", int? supplierId = null)
            => _suppliers.EditInvoice(_admin, invoiceId, supplierId ?? _supplierId, "المندوب علي",
                number, DateTime.Today, lines);

        [Test]
        public void Edit_CorrectsTheHeaderWithoutTouchingTheStock()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 3, 1000m, 1400m));
            int unitsBefore = _stock.GetBatches(panadol).Single().QuantityUnits;

            _suppliers.EditInvoice(_admin, filed.Id, _supplierId, "المندوب سمير", "INV-777",
                new DateTime(2026, 3, 1), LinesOf(filed.Id));

            PurchaseInvoice after = _suppliers.GetInvoice(filed.Id);
            Assert.That(after.InvoiceNumber, Is.EqualTo("INV-777"));
            Assert.That(after.Representative, Is.EqualTo("المندوب سمير"));
            Assert.That(after.InvoiceDate, Is.EqualTo(new DateTime(2026, 3, 1)));
            Assert.That(_stock.GetBatches(panadol).Single().QuantityUnits, Is.EqualTo(unitsBefore),
                "fixing a typed number is not a stock movement");
        }

        [Test]
        public void Edit_CorrectingAQuantity_MovesTheShelfByTheDifference_NotByAssignment()
        {
            int panadol = NewItem("panadol");                       // 10 strips x 10 units = 100 a box
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            StockBatch batch = _stock.GetBatches(panadol).Single();
            Assert.That(batch.QuantityUnits, Is.EqualTo(1000));

            // The pharmacy sells 400 units, then notices the invoice said 10 boxes when 8 arrived.
            _stock.SetBatchQuantity(batch.Id, 600);

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].QuantityBoxes = 8;
            Edit(filed.Id, lines);

            Assert.That(_stock.GetBatch(batch.Id).QuantityUnits, Is.EqualTo(400),
                "two boxes come off what is LEFT (600 - 200); assigning 800 would restock the 400 sold");
        }

        [Test]
        public void Edit_IncreasingAQuantity_AddsTheDifferenceToWhatIsLeft()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 5, 1000m, 1400m));
            StockBatch batch = _stock.GetBatches(panadol).Single();
            _stock.SetBatchQuantity(batch.Id, 100);                  // 400 units sold out of 500

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].QuantityBoxes = 7;                              // two boxes were missed off the paper
            Edit(filed.Id, lines);

            Assert.That(_stock.GetBatch(batch.Id).QuantityUnits, Is.EqualTo(300));
        }

        [Test]
        public void Edit_RefusesAReductionTheShelfCannotPayFor()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            StockBatch batch = _stock.GetBatches(panadol).Single();
            _stock.SetBatchQuantity(batch.Id, 100);                  // 900 of the 1000 units are gone

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].QuantityBoxes = 1;                              // would need 900 units back

            Assert.Throws<ValidationException>(() => Edit(filed.Id, lines),
                "those units are in customers' hands — editing the paperwork does not bring them back");
            Assert.That(_stock.GetBatch(batch.Id).QuantityUnits, Is.EqualTo(100), "and nothing moved");
            Assert.That(_suppliers.GetInvoice(filed.Id).Lines[0].QuantityBoxes, Is.EqualTo(10));
        }

        [Test]
        public void Edit_RecomputesTheTotalAndWhatIsStillOwed()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m));
            Assert.That(filed.Total, Is.EqualTo(10000m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].BoxPurchasePrice = 900m;                        // the price was misread
            Edit(filed.Id, lines);

            PurchaseInvoice after = _suppliers.GetInvoice(filed.Id);
            Assert.That(after.Total, Is.EqualTo(9000m));
            Assert.That(after.Outstanding, Is.EqualTo(9000m));
            Assert.That(_suppliers.Get(_supplierId).Outstanding, Is.EqualTo(9000m),
                "the company's balance is the sum of its invoices, so it follows the correction");
        }

        [Test]
        public void Edit_RefusesATotalBelowWhatHasAlreadyBeenPaid()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(8000m, Line(panadol, 10, 1000m, 1400m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].BoxPurchasePrice = 500m;                        // total would fall to 5000

            Assert.Throws<ValidationException>(() => Edit(filed.Id, lines),
                "money handed over is a fact; the invoice cannot be corrected to owe less than it");
            Assert.That(_suppliers.GetInvoice(filed.Id).Total, Is.EqualTo(10000m));
        }

        [Test]
        public void Edit_AddingAMedicine_PutsItOnTheShelfAsItsOwnBatch()
        {
            int panadol = NewItem("panadol");
            int brufen = NewItem("brufen");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines.Add(Line(brufen, 3, 2000m, 2600m));                // a line missed off the paperwork
            Edit(filed.Id, lines);

            PurchaseInvoice after = _suppliers.GetInvoice(filed.Id);
            Assert.That(after.Lines.Count, Is.EqualTo(2));
            Assert.That(after.Total, Is.EqualTo(2000m + 6000m));
            Assert.That(_stock.GetBatches(brufen).Single().QuantityUnits, Is.EqualTo(300));
        }

        [Test]
        public void Edit_RemovingALine_TakesItsBatchOffTheShelf()
        {
            int panadol = NewItem("panadol");
            int brufen = NewItem("brufen");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m), Line(brufen, 3, 2000m, 2600m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines.RemoveAll(l => l.ItemId == brufen);                // it belonged to another company
            Edit(filed.Id, lines);

            PurchaseInvoice after = _suppliers.GetInvoice(filed.Id);
            Assert.That(after.Lines.Count, Is.EqualTo(1));
            Assert.That(after.Total, Is.EqualTo(2000m));
            Assert.That(_stock.GetBatches(brufen, includeDisposed: true), Is.Empty,
                "stock that was never delivered must not stay on the shelf");
        }

        [Test]
        public void Edit_RemovingALineThatHasBeenSoldFrom_IsRefused()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m), Line(NewItem("brufen"), 1, 900m, 1200m));
            PurchaseInvoiceLine sold = _suppliers.GetInvoice(filed.Id).Lines.First(l => l.ItemId == panadol);
            SellFromBatch(sold.StockBatchId.Value, panadol, 50);

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines.RemoveAll(l => l.ItemId == panadol);

            Assert.Throws<ValidationException>(() => Edit(filed.Id, lines),
                "a sale records which batch it came out of — deleting the batch would orphan it");
            Assert.That(_suppliers.GetInvoice(filed.Id).Lines.Count, Is.EqualTo(2));
        }

        [Test]
        public void Edit_MovingAnInvoiceToAnotherCompany_TakesItsBalanceAndItsPaymentsWithIt()
        {
            int other = _suppliers.CreateSupplier(_admin, "شركة الخرطوم");
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(3000m, Line(panadol, 10, 1000m, 1400m));

            Edit(filed.Id, LinesOf(filed.Id), supplierId: other);

            Assert.That(_suppliers.Get(_supplierId).Outstanding, Is.EqualTo(0m));
            Assert.That(_suppliers.Get(other).Outstanding, Is.EqualTo(7000m));
            Assert.That(_suppliers.GetPayments(filed.Id).Single().SupplierId, Is.EqualTo(other),
                "the payment was made to a company, so it moves with the invoice");
        }

        [Test]
        public void Edit_CorrectingTheCurrentDeliverysPrice_RepricesTheDrugEverywhere()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 1, 1000m, 2000m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].BoxSellingPrice = 3000m;                        // 3000 a box over 100 units = 30
            Edit(filed.Id, lines);

            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(30m));
            Assert.That(_stock.GetBatches(panadol).Single().BoxSellingPrice, Is.EqualTo(3000m));
        }

        [Test]
        public void Edit_CorrectingAnOldInvoice_LeavesTodaysPriceAlone()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice old = Record(0m, Line(panadol, 1, 1000m, 2000m));
            _suppliers.RecordInvoice(_admin, _supplierId, null, "INV-NEW", DateTime.Today,
                new[] { Line(panadol, 1, 1200m, 2500m) });           // today's delivery sets the price
            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(25m));

            // Fixing a figure on the OLD paperwork must not drag the shelf back to last season's price.
            List<PurchaseInvoiceLine> lines = LinesOf(old.Id);
            lines[0].BoxSellingPrice = 1800m;
            Edit(old.Id, lines, number: "INV-OLD");

            Assert.That(_items.GetById(panadol).SellingPrice, Is.EqualTo(25m),
                "the catalog price came from the later delivery and stays there");
            StockBatch corrected = _stock.GetBatch(_suppliers.GetInvoice(old.Id).Lines[0].StockBatchId.Value);
            Assert.That(corrected.BoxSellingPrice, Is.EqualTo(1800m),
                "only the batch the correction was about changes");
        }

        [Test]
        public void Edit_KeepsTheNameTheDrugWasDeliveredUnder()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));

            Item item = _items.GetById(panadol);
            item.NameEn = "panadol extra";
            _items.Update(item);

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].QuantityBoxes = 3;
            Edit(filed.Id, lines);

            Assert.That(_suppliers.GetInvoice(filed.Id).Lines[0].ItemName, Does.Not.Contain("extra"),
                "correcting a quantity must not rewrite the name the boxes arrived under");
        }

        [Test]
        public void Edit_ByAPlainCashier_IsAllowed()
        {
            // Correcting a delivery is part of filing one (V2.4): whoever mistyped a quantity while the
            // driver waited is the person standing there when the mistake is noticed.
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));

            Assert.DoesNotThrow(() => _suppliers.EditInvoice(
                _cashier, filed.Id, _supplierId, null, "X", DateTime.Today, LinesOf(filed.Id)));
        }

        [Test]
        public void Edit_ByAPrivilegedEmployee_IsAllowed()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines[0].QuantityBoxes = 4;
            _suppliers.EditInvoice(_privileged, filed.Id, _supplierId, null, "INV-100",
                DateTime.Today, lines);

            Assert.That(_suppliers.GetInvoice(filed.Id).Lines[0].QuantityBoxes, Is.EqualTo(4),
                "the person who typed the delivery in is the person who spots the typo");
        }

        [Test]
        public void Edit_ToAnEmptyInvoice_IsRefused()
        {
            int panadol = NewItem("panadol");
            PurchaseInvoice filed = Record(0m, Line(panadol, 2, 1000m, 1400m));

            Assert.Throws<ValidationException>(() => Edit(filed.Id, new List<PurchaseInvoiceLine>()),
                "an invoice with nothing on it is a deletion wearing a correction's clothes");
        }

        [Test]
        public void Edit_IsRolledBackWhole_WhenOneLineIsImpossible()
        {
            int panadol = NewItem("panadol");
            int brufen = NewItem("brufen");
            PurchaseInvoice filed = Record(0m, Line(panadol, 10, 1000m, 1400m), Line(brufen, 5, 2000m, 2600m));
            PurchaseInvoiceLine panadolLine = _suppliers.GetInvoice(filed.Id).Lines.First(l => l.ItemId == panadol);
            _stock.SetBatchQuantity(panadolLine.StockBatchId.Value, 50);     // 950 units sold

            List<PurchaseInvoiceLine> lines = LinesOf(filed.Id);
            lines.First(l => l.ItemId == brufen).QuantityBoxes = 9;          // fine on its own
            lines.First(l => l.ItemId == panadol).QuantityBoxes = 1;         // impossible

            Assert.Throws<ValidationException>(() => Edit(filed.Id, lines));

            PurchaseInvoice after = _suppliers.GetInvoice(filed.Id);
            Assert.That(after.Lines.First(l => l.ItemId == brufen).QuantityBoxes, Is.EqualTo(5),
                "the whole correction is one transaction — a half-applied one would be worse than none");
            Assert.That(after.Total, Is.EqualTo(10000m + 10000m));
            Assert.That(_stock.GetBatches(brufen).Single().QuantityUnits, Is.EqualTo(500));
        }

        /// <summary>
        /// Takes units off a batch the way a sale does — including the allocation row that records which
        /// batch the sale came out of, which is what makes the batch undeletable afterwards.
        /// </summary>
        private void SellFromBatch(int batchId, int itemId, int units)
        {
            using (DbConnection conn = _db.OpenConnection())
            {
                int saleId = Insert(conn, "INSERT INTO sales (user_id) VALUES (" + _admin.Id + ")");
                int lineId = Insert(conn,
                    "INSERT INTO sale_lines (sale_id, item_id, quantity, units_each, unit_price, line_total) " +
                    "VALUES (" + saleId + ", " + itemId + ", " + units + ", 1, 0, 0)");
                Insert(conn,
                    "INSERT INTO sale_line_allocations (sale_line_id, batch_id, units, unit_cost) " +
                    "VALUES (" + lineId + ", " + batchId + ", " + units + ", 0)");
            }

            StockBatch batch = _stock.GetBatch(batchId);
            _stock.SetBatchQuantity(batchId, batch.QuantityUnits - units);
        }

        /// <summary>Raw insert returning the new id. The repositories' own helper is internal to the core
        /// assembly, and these rows exist only to stand in for a sale the POS would have made.</summary>
        private static int Insert(DbConnection conn, string sql)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT last_insert_rowid()";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
