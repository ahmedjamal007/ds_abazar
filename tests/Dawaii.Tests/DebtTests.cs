using System;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class DebtTests
    {
        private FakeCustomerRepository _customers;
        private FakeDebtStore _store;
        private DebtService _svc;
        private User _admin, _cashier, _privileged;
        private int _cust;

        [SetUp]
        public void SetUp()
        {
            _customers = new FakeCustomerRepository();
            _store = new FakeDebtStore(_customers);
            _svc = new DebtService(_customers, _store);
            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };
            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };
            _privileged = new User { Id = 3, Role = Role.FullEmployee, IsActive = true };
            _cust = _customers.Add(new Customer { Name = "أحمد" });
        }

        [Test]
        public void Charge_Then_Payment_ComputesBalance()
        {
            _store.RecordManualCharge(_cust, 300m, _admin.Id, "بيع آجل");
            _store.RecordPayment(_cust, 100m, _cashier.Id, "دفعة");

            Assert.That(_customers.GetById(_cust).Balance, Is.EqualTo(200m));
            Assert.That(DebtLedger.Balance(_customers.GetTransactions(_cust)), Is.EqualTo(200m));
        }

        [Test]
        public void Statement_HasRunningBalance()
        {
            _store.RecordManualCharge(_cust, 300m, _admin.Id, "1");
            _store.RecordPayment(_cust, 50m, _admin.Id, "2");
            _store.RecordManualCharge(_cust, 25m, _admin.Id, "3");

            var rows = _svc.GetStatement(_cust);
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].RunningBalance, Is.EqualTo(300m));
            Assert.That(rows[1].RunningBalance, Is.EqualTo(250m));
            Assert.That(rows[2].RunningBalance, Is.EqualTo(275m));
        }

        [Test]
        public void RecordPayment_ByCashier_Allowed()
        {
            _store.RecordManualCharge(_cust, 100m, _admin.Id, "دين");
            Assert.DoesNotThrow(() => _svc.RecordPayment(_cashier, _cust, 40m));
            Assert.That(_customers.GetById(_cust).Balance, Is.EqualTo(60m));
        }

        [Test]
        public void RecordPayment_NonPositive_Throws()
        {
            Assert.Throws<ValidationException>(() => _svc.RecordPayment(_cashier, _cust, 0m));
        }

        [Test]
        public void ManualCharge_ByCashier_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.RecordManualCharge(_cashier, _cust, 50m, "x"));
        }

        [Test]
        public void Overpayment_ProducesNegativeBalance()
        {
            _store.RecordManualCharge(_cust, 100m, _admin.Id, "دين");
            _svc.RecordPayment(_admin, _cust, 150m);
            Assert.That(_customers.GetById(_cust).Balance, Is.EqualTo(-50m)); // store credit (D-06)
        }

        [Test]
        public void TotalOutstanding_SumsPositiveBalancesOnly()
        {
            int c2 = _customers.Add(new Customer { Name = "سارة" });
            _store.RecordManualCharge(_cust, 200m, _admin.Id, "د");
            _store.RecordManualCharge(c2, 100m, _admin.Id, "د");
            _svc.RecordPayment(_admin, c2, 300m); // c2 now -200 (excluded)

            Assert.That(_svc.TotalOutstanding(), Is.EqualTo(200m));
        }

        // ---------------- V1.9: editing and deleting customers ----------------

        [Test]
        public void UpdateCustomer_ChangesNameAndPhone()
        {
            _svc.UpdateCustomer(_admin, _cust, "أحمد جمال", "0912345678");

            Customer c = _customers.GetById(_cust);
            Assert.That(c.Name, Is.EqualTo("أحمد جمال"));
            Assert.That(c.Phone, Is.EqualTo("0912345678"));
        }

        [Test]
        public void UpdateCustomer_BlankName_Throws()
        {
            Assert.Throws<ValidationException>(() => _svc.UpdateCustomer(_admin, _cust, "   ", "091"));
        }

        [Test]
        public void UpdateOrDeleteCustomer_ByCashier_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.UpdateCustomer(_cashier, _cust, "اسم", null));
            Assert.Throws<PermissionDeniedException>(() => _svc.DeleteCustomer(_cashier, _cust));
        }

        /// <summary>V2.0 opened the ledger screen to "موظف ذو امتيازات". What they may do there is the
        /// day-to-day of the counter — open an account, take money against it — and no more: the account
        /// holder's own record, and a debt raised by hand, stay the manager's however the screen is drawn.</summary>
        [Test]
        public void PrivilegedEmployee_RunsTheLedger_ButNotTheManagersEntries()
        {
            Assert.That(_privileged.CanManageCustomers, Is.True, "the ledger screen is theirs to open");

            int added = _svc.CreateCustomer(_cashier, "سالم", "0911111111");
            _store.RecordManualCharge(added, 120m, _admin.Id, "بيع آجل");
            Assert.DoesNotThrow(() => _svc.RecordPayment(_privileged, added, 50m));
            Assert.That(_customers.GetById(added).Balance, Is.EqualTo(70m));

            Assert.Throws<PermissionDeniedException>(() => _svc.UpdateCustomer(_privileged, _cust, "اسم", null));
            Assert.Throws<PermissionDeniedException>(() => _svc.DeleteCustomer(_privileged, _cust));
            Assert.Throws<PermissionDeniedException>(() => _svc.RecordManualCharge(_privileged, _cust, 50m, "x"));
        }

        [Test]
        public void PlainCashier_DoesNotGetTheLedgerScreen()
        {
            Assert.That(_cashier.CanManageCustomers, Is.False, "a cashier sells; the accounts are not their screen");
            Assert.That(_admin.CanManageCustomers, Is.True);
        }

        [Test]
        public void DeleteCustomer_RemovesOneWithNoHistory()
        {
            int fresh = _customers.Add(new Customer { Name = "عميل جديد" });
            Assert.That(_svc.DeleteCustomer(_admin, fresh), Is.True);
            Assert.That(_customers.GetById(fresh), Is.Null);
        }

        [Test]
        public void DeleteCustomer_WithLedgerHistory_IsRefused()
        {
            // Charged then fully settled: the balance is clear, but the ledger still names them.
            _store.RecordManualCharge(_cust, 100m, _admin.Id, "دين");
            _svc.RecordPayment(_admin, _cust, 100m);

            Assert.That(_customers.GetById(_cust).Balance, Is.EqualTo(0m));
            Assert.That(_svc.DeleteCustomer(_admin, _cust), Is.False);
            Assert.That(_customers.GetById(_cust), Is.Not.Null);
        }

        [Test]
        public void DeleteCustomer_WhoStillOwesMoney_Throws()
        {
            _store.RecordManualCharge(_cust, 250m, _admin.Id, "دين");
            var ex = Assert.Throws<ValidationException>(() => _svc.DeleteCustomer(_admin, _cust));
            Assert.That(ex.Message, Does.Contain("رصيد"));
        }
    }
}
