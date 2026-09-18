using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class EmployeeServiceTests
    {
        private FakeEmployeeRepository _repo;
        private FakeUserRepository _users;
        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeCustomerRepository _customers;
        private FakeSaleStore _sales;
        private FakeAuditRepository _audit;
        private EmployeeService _svc;
        private PosService _pos;
        private User _admin, _emp;
        private int _panadol;

        [SetUp]
        public void SetUp()
        {
            _users = new FakeUserRepository();
            _items = new FakeItemRepository();
            _stock = new FakeStockRepository();
            _repo = new FakeEmployeeRepository(_stock);
            _customers = new FakeCustomerRepository();
            _sales = new FakeSaleStore(_stock, _customers);
            _audit = new FakeAuditRepository();
            _svc = new EmployeeService(_repo, _users, _items, _stock, _sales, _audit);
            _pos = new PosService(_items, _stock, _sales, _customers,
                new FakeSettingsRepository().Seed("pos_expiry_warn_days", "30"), _audit);

            _admin = new User { Username = "admin", Role = Role.Admin, IsActive = true };
            _admin.Id = _users.Add(_admin);
            _emp = new User { Username = "sara", Role = Role.Cashier, IsActive = true };
            _emp.Id = _users.Add(_emp);

            _panadol = _items.Add(new Item { NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m, IsActive = true });
            _stock.AddBatch(new StockBatch { ItemId = _panadol, QuantityUnits = 500, ExpiryDate = DateTime.Today.AddYears(1), BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 });
        }

        [Test]
        public void RecordLogin_AppearsInDailyReport_AsFirstClockIn()
        {
            _svc.RecordLogin(_emp, "t1");
            System.Threading.Thread.Sleep(15);
            _svc.RecordLogin(_emp, "t1"); // second login same day — first one wins

            var row = _svc.DailyReport(_admin, DateTime.Today).Single(r => r.User.Id == _emp.Id);
            Assert.That(row.FirstLoginAt, Is.Not.Null);
            Assert.That(row.FirstLoginAt, Is.EqualTo(_repo.Attendance.Min(a => a.LoginAt)));
        }

        [Test]
        public void DailyReport_SumsEmployeeSales()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");    // 20 units * 2 = 40
            _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t1");  // admin's own sale

            var rows = _svc.DailyReport(_admin, DateTime.Today);
            Assert.That(rows.Single(r => r.User.Id == _emp.Id).SalesTotal, Is.EqualTo(40m));
            Assert.That(rows.Single(r => r.User.Id == _emp.Id).SalesCount, Is.EqualTo(1));
            Assert.That(rows.Single(r => r.User.Id == _admin.Id).SalesTotal, Is.EqualTo(40m));
        }

        [Test]
        public void Expenses_MoneyAndMedicine_ShowOnDailyReport()
        {
            _svc.AddMoneyExpense(_emp, _emp.Id, 50m, "سلفة");
            _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 10, "استخدام شخصي"); // 10 * 2 = 20

            var row = _svc.DailyReport(_admin, DateTime.Today).Single(r => r.User.Id == _emp.Id);
            Assert.That(row.MoneyExpenses, Is.EqualTo(50m));
            Assert.That(row.MedicineExpenses, Is.EqualTo(20m));
            Assert.That(row.TotalExpenses, Is.EqualTo(70m));
        }

        [Test]
        public void MedicineExpense_MoreThanAvailableStock_Throws_AndRecordsNothing()
        {
            var ex = Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 501, "استخدام شخصي"));

            Assert.That(ex.RequestedUnits, Is.EqualTo(501));
            Assert.That(ex.AvailableUnits, Is.EqualTo(500));
            Assert.That(ex.Message, Does.Contain("بنادول"));
            Assert.That(_repo.Expenses, Is.Empty, "A refused expense must not be logged.");
        }

        [Test]
        public void MedicineExpense_ItemWithNoStock_Throws()
        {
            int brufen = _items.Add(new Item { NameEn = "بروفين", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 3m, IsActive = true });

            var ex = Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, brufen, 1, "استخدام شخصي"));
            Assert.That(ex.AvailableUnits, Is.EqualTo(0));
            Assert.That(_repo.Expenses, Is.Empty);
        }

        [Test]
        public void MedicineExpense_DisposedBatchStock_DoesNotCount()
        {
            int vitc = _items.Add(new Item { NameEn = "فيتامين سي", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 5m, IsActive = true });
            int batchId = _stock.AddBatch(new StockBatch { ItemId = vitc, QuantityUnits = 20, BoxPurchasePrice = 2m, StripsPerBox = 1, UnitsPerStrip = 1 });
            _stock.DisposeBatch(batchId);

            Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, vitc, 1, "استخدام شخصي"));
        }

        [Test]
        public void MedicineExpense_UpToAvailableStock_Succeeds()
        {
            Assert.DoesNotThrow(() => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 500, "كل المتوفر"));
            Assert.That(_repo.Expenses.Single().Amount, Is.EqualTo(1000m));   // 500 × 2
        }

        [Test]
        public void MedicineExpense_StockSoldByThePos_LowersWhatTheEmployeeMayTake()
        {
            // 49 strips × 10 units = 490 units sold out of 500, leaving 10 on the shelf.
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 49 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");

            Assert.That(_svc.AvailableUnits(_panadol), Is.EqualTo(10));
            Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 11, "أكثر من المتبقي"));
            Assert.DoesNotThrow(() => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 10, "المتبقي"));
        }

        // ---------------- the medicine actually leaves the shelf (V2.3) ----------------
        //
        // Until V2.3 a medicine expense checked stock and then never wrote to it: the drug walked out of
        // the pharmacy and the database went on counting it. Every test below fails against that version,
        // which is the point — the old tests all passed against it because they only ever asserted on the
        // expense row and on the *rejection* path.

        [Test]
        public void MedicineExpense_TakesTheUnitsOffTheShelf()
        {
            _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 30, "استخدام شخصي");

            Assert.That(_svc.AvailableUnits(_panadol), Is.EqualTo(470),
                "the medicine left the pharmacy, so it must leave the stock count with it");
        }

        [Test]
        public void MedicineExpense_LowersWhatThePosCanThenSell()
        {
            _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 495, "معظم المتوفر");

            // 5 units left: one strip (10 units) can no longer be sold.
            var oneStrip = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 1 } };
            Assert.Throws<InsufficientStockException>(
                () => _pos.Complete(_emp, oneStrip, SaleType.Cash, null, 0m, "t1"),
                "stock taken by staff must not still be sellable at the counter");
        }

        [Test]
        public void MedicineExpense_CannotBeRepeatedForeverOnTheSameStock()
        {
            for (int i = 0; i < 5; i++)
                _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 100, "دفعة " + i);

            Assert.That(_svc.AvailableUnits(_panadol), Is.Zero);
            Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 1, "بعد نفاد المخزون"),
                "the availability check has to tighten as stock is taken, or it checks nothing at all");
        }

        [Test]
        public void MedicineExpense_TakesFromTheBatchThatExpiresSoonest()
        {
            int brufen = _items.Add(new Item { NameEn = "brufen", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 3m, IsActive = true });
            int later = _stock.AddBatch(new StockBatch { ItemId = brufen, QuantityUnits = 50, ExpiryDate = DateTime.Today.AddYears(2), StripsPerBox = 1, UnitsPerStrip = 1 });
            int sooner = _stock.AddBatch(new StockBatch { ItemId = brufen, QuantityUnits = 50, ExpiryDate = DateTime.Today.AddMonths(1), StripsPerBox = 1, UnitsPerStrip = 1 });

            _svc.AddMedicineExpense(_emp, _emp.Id, brufen, 20, "استخدام شخصي");

            Assert.That(_stock.GetBatch(sooner).QuantityUnits, Is.EqualTo(30), "FEFO — nearest expiry first");
            Assert.That(_stock.GetBatch(later).QuantityUnits, Is.EqualTo(50), "the later batch is untouched");
        }

        [Test]
        public void MedicineExpense_LeavesAnAdjustmentTrailNamingTheEmployee()
        {
            _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 25, "استخدام شخصي");

            StockAdjustment adj = _stock.Adjustments.Single();
            Assert.That(adj.ItemId, Is.EqualTo(_panadol));
            Assert.That(adj.DeltaUnits, Is.EqualTo(-25), "a signed loss, the way a disposal records one");
            Assert.That(adj.UserId, Is.EqualTo(_emp.Id), "shrinkage has to be attributable");
        }

        [Test]
        public void MedicineExpense_ThatIsRefused_TakesNothingOffTheShelf()
        {
            Assert.Throws<InsufficientStockException>(
                () => _svc.AddMedicineExpense(_emp, _emp.Id, _panadol, 501, "أكثر من المتوفر"));

            Assert.That(_svc.AvailableUnits(_panadol), Is.EqualTo(500));
            Assert.That(_repo.Expenses, Is.Empty);
            Assert.That(_stock.Adjustments, Is.Empty);
        }

        [Test]
        public void Expense_ForAnotherEmployee_DeniedUnlessAdmin()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.AddMoneyExpense(_emp, _admin.Id, 10m, "x"));
            Assert.DoesNotThrow(() => _svc.AddMoneyExpense(_admin, _emp.Id, 10m, "من المدير"));
        }

        // ---------------- "مبيعاتي اليوم": the employee's own day, and only their own ----------------

        [Test]
        public void MyDay_ReturnsOnlyTheSignedInEmployeesSales()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");     // sara's
            _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t1");   // someone else's
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");     // sara's

            var sheet = _svc.MyDay(_emp, DateTime.Today);
            Assert.That(sheet.SalesCount, Is.EqualTo(2));
            Assert.That(sheet.Sales.All(s => s.UserId == _emp.Id), Is.True, "A colleague's invoice must never appear.");
            Assert.That(sheet.SalesTotal, Is.EqualTo(80m));   // 2 × (20 units × 2)
        }

        [Test]
        public void MyDay_ReturnsOnlyTheSignedInEmployeesExpenses()
        {
            _svc.AddMoneyExpense(_emp, _emp.Id, 50m, "سلفة");
            _svc.AddMoneyExpense(_admin, _admin.Id, 900m, "مصروف المدير");

            var sheet = _svc.MyDay(_emp, DateTime.Today);
            Assert.That(sheet.ExpensesCount, Is.EqualTo(1));
            Assert.That(sheet.Expenses.All(e => e.UserId == _emp.Id), Is.True);
            Assert.That(sheet.ExpensesTotal, Is.EqualTo(50m));
        }

        [Test]
        public void MyDay_IgnoresOtherDays()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 1 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");

            Assert.That(_svc.MyDay(_emp, DateTime.Today).SalesCount, Is.EqualTo(1));
            Assert.That(_svc.MyDay(_emp, DateTime.Today.AddDays(-1)).SalesCount, Is.EqualTo(0));
        }

        [Test]
        public void MyDay_ExcludesFullyReturnedInvoices()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 } };
            Sale kept = _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");
            Sale undone = _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");
            _pos.ReturnSale(_emp, undone.Id, "إرجاع");

            var sheet = _svc.MyDay(_emp, DateTime.Today);
            Assert.That(sheet.Sales.Select(s => s.Id), Is.EquivalentTo(new[] { kept.Id }));
        }

        [Test]
        public void Employee_CannotReadAnotherEmployeesSalesOrExpenses()
        {
            Assert.Throws<PermissionDeniedException>(
                () => _svc.SalesIn(_emp, DateTime.Today, DateTime.Today.AddDays(1), _admin.Id));
            Assert.Throws<PermissionDeniedException>(
                () => _svc.ExpensesIn(_emp, DateTime.Today, DateTime.Today.AddDays(1), _admin.Id));
            Assert.Throws<PermissionDeniedException>(
                () => _svc.DaySheet(_emp, _admin.Id, DateTime.Today, DateTime.Today.AddDays(1)));
        }

        [Test]
        public void Employee_CannotReadThePharmacyWideExpenseList()
        {
            Assert.Throws<PermissionDeniedException>(
                () => _svc.ExpensesIn(_emp, DateTime.Today, DateTime.Today.AddDays(1)));
            Assert.Throws<PermissionDeniedException>(
                () => _svc.ExpensesOn(_emp, DateTime.Today));
            Assert.DoesNotThrow(() => _svc.ExpensesIn(_admin, DateTime.Today, DateTime.Today.AddDays(1)));
        }

        [Test]
        public void Admin_DrillDown_StillSeesAnyEmployeesDay()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");
            _svc.AddMoneyExpense(_emp, _emp.Id, 25m, "سلفة");

            var sheet = _svc.DaySheet(_admin, _emp.Id, DateTime.Today, DateTime.Today.AddDays(1));
            Assert.That(sheet.SalesTotal, Is.EqualTo(40m));
            Assert.That(sheet.ExpensesTotal, Is.EqualTo(25m));
        }

        [Test]
        public void MyDay_WithoutASignedInUser_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.MyDay(null, DateTime.Today));
        }

        [Test]
        public void NetSalary_SubtractsThisMonthsDeductions()
        {
            _svc.SetSalary(_admin, _emp.Id, 3000m);
            _svc.AddDeduction(_admin, _emp.Id, 200m, "غياب");
            _svc.AddDeduction(_admin, _emp.Id, 100m, "تأخير");

            Assert.That(_svc.NetSalaryFor(_emp.Id, DateTime.Today.Year, DateTime.Today.Month), Is.EqualTo(2700m));
        }

        [Test]
        public void SetSalary_ByEmployee_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.SetSalary(_emp, _emp.Id, 9999m));
        }

        [Test]
        public void AddLeave_InvalidRange_Throws()
        {
            Assert.Throws<ValidationException>(
                () => _svc.AddLeave(_admin, _emp.Id, DateTime.Today, DateTime.Today.AddDays(-3), "x"));
        }

        [Test]
        public void DailyReport_ByEmployee_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.DailyReport(_emp, DateTime.Today));
        }

        [Test]
        public void RangeReport_CountsDistinctAttendanceDays()
        {
            // two logins today + one "yesterday" = 2 attendance days in the week
            _svc.RecordLogin(_emp, "t1");
            _svc.RecordLogin(_emp, "t1");
            _repo.AddAttendance(new Dawaii.Core.Models.AttendanceEntry
            { UserId = _emp.Id, LoginAt = DateTime.Today.AddDays(-1).AddHours(9) });

            var row = _svc.RangeReport(_admin, DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1))
                          .Single(r => r.User.Id == _emp.Id);
            Assert.That(row.AttendanceDays, Is.EqualTo(2));
            Assert.That(row.FirstLoginAt.Value.Date, Is.EqualTo(DateTime.Today.AddDays(-1)), "First login in the range wins.");
        }

        [Test]
        public void RangeReport_SumsSalesAndExpensesAcrossPeriod()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 1 } };
            _pos.Complete(_emp, cart, SaleType.Cash, null, 0m, "t1");   // 20 today
            _svc.AddMoneyExpense(_emp, _emp.Id, 15m, "سلفة");

            var row = _svc.RangeReport(_admin, DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1))
                          .Single(r => r.User.Id == _emp.Id);
            Assert.That(row.SalesTotal, Is.EqualTo(20m));
            Assert.That(row.MoneyExpenses, Is.EqualTo(15m));
        }
    }
}
