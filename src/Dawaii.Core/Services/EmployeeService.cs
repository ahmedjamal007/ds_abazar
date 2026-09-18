using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>One employee's line on the daily report (V1.2 req 2): sales, expenses, clock-in.</summary>
    public class EmployeeDayRow
    {
        public User User { get; set; }
        public DateTime? FirstLoginAt { get; set; }

        /// <summary>Distinct days with at least one login inside the period (for weekly/monthly reports).</summary>
        public int AttendanceDays { get; set; }
        public int SalesCount { get; set; }
        public decimal SalesTotal { get; set; }
        public decimal MoneyExpenses { get; set; }
        public decimal MedicineExpenses { get; set; }
        public decimal TotalExpenses => MoneyExpenses + MedicineExpenses;
    }

    /// <summary>
    /// One employee's invoices and expenses for a period, with the totals both the manager drill-down
    /// and the employee's own daily-sales screen print in their summary bar.
    /// </summary>
    public class EmployeeDaySheet
    {
        public IReadOnlyList<Sale> Sales { get; set; } = new List<Sale>();
        public IReadOnlyList<EmployeeExpense> Expenses { get; set; } = new List<EmployeeExpense>();

        /// <summary>When the employee first signed in during the period, or null if they never did.
        /// The printed daily summary heads with it (V2.3).</summary>
        public DateTime? FirstLoginAt { get; set; }

        public int SalesCount => Sales.Count;
        public decimal SalesTotal => Sales.Sum(s => s.Total);
        public int ExpensesCount => Expenses.Count;
        public decimal ExpensesTotal => Expenses.Sum(e => e.Amount);

        /// <summary>Money given back on this period's invoices, whole or partial.</summary>
        public decimal ReturnedTotal => Sales.Sum(s => s.RefundedTotal);

        /// <summary>What the invoices actually brought in after returns.</summary>
        public decimal NetTotal => Sales.Sum(s => s.NetTotal);

        /// <summary>
        /// The takings split by how they were paid — one row per payment method, in the order the POS
        /// offers them, plus a row for credit (V2.3). Every method appears even at zero, so a printed
        /// summary always has the same shape and a missing line is never mistaken for a missing sale.
        /// Amounts are NET of returns, so the rows add up to <see cref="NetTotal"/>.
        /// </summary>
        public IReadOnlyList<PaymentBreakdownRow> ByPaymentMethod
        {
            get
            {
                var rows = new List<PaymentBreakdownRow>();
                foreach (var m in PaymentMethods.All)
                {
                    var group = Sales.Where(s => s.SaleType != SaleType.Credit &&
                                                 PaymentMethods.Normalize(s.PaymentMethod) == m.Code).ToList();
                    rows.Add(new PaymentBreakdownRow
                    {
                        Code = m.Code, LabelAr = m.LabelAr,
                        Count = group.Count, Total = group.Sum(s => s.NetTotal)
                    });
                }
                var credit = Sales.Where(s => s.SaleType == SaleType.Credit).ToList();
                rows.Add(new PaymentBreakdownRow
                {
                    Code = "Credit", LabelAr = "آجل", Count = credit.Count, Total = credit.Sum(s => s.NetTotal)
                });
                return rows;
            }
        }
    }

    /// <summary>One line of the payment-method table: how many invoices, and how much, for one way of paying.</summary>
    public class PaymentBreakdownRow
    {
        public string Code { get; set; }
        public string LabelAr { get; set; }
        public int Count { get; set; }
        public decimal Total { get; set; }
    }

    /// <summary>
    /// Employee affairs (V1.2 req 2+5): attendance recorded automatically at login, salaries,
    /// leaves, deductions, expenses (money or medicine) and the per-employee daily report.
    /// HR management is Admin-only; an employee may log their own expenses.
    /// </summary>
    public class EmployeeService
    {
        private readonly IEmployeeRepository _employees;
        private readonly IUserRepository _users;
        private readonly IItemRepository _items;
        private readonly IStockRepository _stock;
        private readonly ISaleStore _sales;
        private readonly IAuditRepository _audit;

        public EmployeeService(IEmployeeRepository employees, IUserRepository users,
            IItemRepository items, IStockRepository stock, ISaleStore sales, IAuditRepository audit)
        {
            _employees = employees;
            _users = users;
            _items = items;
            _stock = stock;
            _sales = sales;
            _audit = audit;
        }

        // ---------------- Attendance ----------------

        /// <summary>Called right after a successful login — records the clock-in automatically.</summary>
        public void RecordLogin(User user, string terminal)
        {
            if (user == null) return;
            _employees.AddAttendance(new AttendanceEntry { UserId = user.Id, LoginAt = DateTime.Now, Terminal = terminal });
        }

        public IReadOnlyList<AttendanceEntry> AttendanceOn(DateTime day, int? userId = null)
            => _employees.GetAttendance(day.Date, day.Date.AddDays(1), userId);

        // ---------------- HR (Admin) ----------------

        public EmployeeProfile GetProfile(int userId)
            => _employees.GetProfile(userId) ?? new EmployeeProfile { UserId = userId };

        public void SetSalary(User admin, int userId, decimal monthlySalary, string notes = null)
        {
            Guard.RequireAdmin(admin, "إدارة الرواتب متاحة للمدير فقط.");
            if (monthlySalary < 0) throw new ValidationException("الراتب غير صالح.");
            _employees.UpsertProfile(new EmployeeProfile { UserId = userId, MonthlySalary = monthlySalary, Notes = notes });
            _audit.Log(admin, "SetSalary", "employee_profiles", userId, monthlySalary.ToString("0.00"));
        }

        public int AddLeave(User admin, int userId, DateTime from, DateTime to, string reason)
        {
            Guard.RequireAdmin(admin, "إدارة الإجازات متاحة للمدير فقط.");
            if (to.Date < from.Date) throw new ValidationException("نهاية الإجازة قبل بدايتها.");
            int id = _employees.AddLeave(new LeaveEntry
            {
                UserId = userId, FromDate = from.Date, ToDate = to.Date,
                Reason = reason, CreatedBy = admin.Id, CreatedAt = DateTime.Now
            });
            _audit.Log(admin, "AddLeave", "leaves", id, $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
            return id;
        }

        public IReadOnlyList<LeaveEntry> Leaves(int userId) => _employees.GetLeaves(userId);

        public int AddDeduction(User admin, int userId, decimal amount, string reason)
        {
            Guard.RequireAdmin(admin, "إدارة الخصومات متاحة للمدير فقط.");
            if (amount <= 0) throw new ValidationException("مبلغ الخصم يجب أن يكون أكبر من صفر.");
            if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("سبب الخصم مطلوب.");
            int id = _employees.AddDeduction(new DeductionEntry
            {
                UserId = userId, Amount = decimal.Round(amount, 2), Reason = reason.Trim(),
                CreatedBy = admin.Id, CreatedAt = DateTime.Now
            });
            _audit.Log(admin, "AddDeduction", "deductions", id, $"{amount:0.00}: {reason}");
            return id;
        }

        public IReadOnlyList<DeductionEntry> Deductions(int userId)
            => _employees.GetDeductions(userId);

        /// <summary>Net pay for a month: salary − deductions recorded inside that month.</summary>
        public decimal NetSalaryFor(int userId, int year, int month)
        {
            var from = new DateTime(year, month, 1);
            decimal salary = GetProfile(userId).MonthlySalary;
            decimal deducted = _employees.GetDeductions(userId, from, from.AddMonths(1)).Sum(d => d.Amount);
            return decimal.Round(salary - deducted, 2);
        }

        // ---------------- Expenses (req 5) ----------------

        /// <summary>An employee logs money taken; admins may log for anyone.</summary>
        public int AddMoneyExpense(User actor, int userId, decimal amount, string note)
        {
            RequireSelfOrAdmin(actor, userId);
            if (amount <= 0) throw new ValidationException("المبلغ يجب أن يكون أكبر من صفر.");
            int id = _employees.AddExpense(new EmployeeExpense
            {
                UserId = userId, Type = ExpenseType.Money, Amount = decimal.Round(amount, 2),
                Note = note, CreatedAt = DateTime.Now
            });
            _audit.Log(actor, "EmployeeExpense", "employee_expenses", id, $"money {amount:0.00}");
            return id;
        }

        /// <summary>
        /// An employee logs medicine taken; the value is units × current selling price.
        /// The medicine actually leaves the shelf, so the request is checked against live stock first
        /// and refused when it asks for more units than the item has (or when it has none at all).
        /// </summary>
        public int AddMedicineExpense(User actor, int userId, int itemId, int units, string note)
        {
            RequireSelfOrAdmin(actor, userId);
            if (units <= 0) throw new ValidationException("الكمية يجب أن تكون أكبر من صفر.");
            Item item = _items.GetById(itemId);
            if (item == null) throw new ValidationException("الصنف غير موجود.");

            if (!item.SellingPrice.HasValue)
                throw new ValidationException("الصنف بدون سعر بيع — استلم مخزوناً له أولاً ليُحسب سعره تلقائياً.");

            IReadOnlyList<StockBatch> batches = _stock.GetSellableBatches(itemId);
            int available = FefoAllocator.AvailableUnits(batches);
            if (available <= 0)
                throw new InsufficientStockException(itemId, units, 0,
                    $"\"{item.NameEn}\" غير متوفر في المخزون.");
            if (units > available)
                throw new InsufficientStockException(itemId, units, available,
                    $"الكمية غير كافية من \"{item.NameEn}\" — المطلوب {units} حبة، المتوفر {available} حبة.");

            // The medicine leaves the shelf, so it comes off the same batches a sale would take it
            // from — nearest expiry first — and the expense row and the decrement commit together.
            // Until V2.3 only the check above existed: stock was read, never written, so recorded
            // stock drifted above physical stock by everything the staff ever took.
            IReadOnlyList<BatchAllocation> allocations = FefoAllocator.Allocate(itemId, batches, units);

            decimal value = decimal.Round(item.SellingPrice.Value * units, 2);
            int id = _employees.AddMedicineExpense(new EmployeeExpense
            {
                UserId = userId, Type = ExpenseType.Medicine, Amount = value,
                ItemId = itemId, Units = units, Note = note, CreatedAt = DateTime.Now
            }, allocations);
            _audit.Log(actor, "EmployeeExpense", "employee_expenses", id, $"medicine {item.NameEn} x{units} = {value:0.00}");
            return id;
        }

        public IReadOnlyList<EmployeeExpense> ExpensesOn(User actor, DateTime day, int? userId = null)
            => ExpensesIn(actor, day.Date, day.Date.AddDays(1), userId);

        /// <summary>
        /// Logged expenses in the period. Asking for one employee follows the same rule as logging one —
        /// an employee may read their own, an admin may read anyone's; asking for the whole pharmacy
        /// (<paramref name="userId"/> null) is a manager view and requires Admin.
        /// </summary>
        public IReadOnlyList<EmployeeExpense> ExpensesIn(User actor, DateTime fromInclusive, DateTime toExclusive, int? userId = null)
        {
            if (userId.HasValue) RequireSelfOrAdmin(actor, userId.Value, "يمكن للموظف مراجعة مصروفاته الخاصة فقط.");
            else Guard.RequireAdmin(actor, "تقرير المصروفات متاح للمدير فقط.");
            return _employees.GetExpenses(fromInclusive, toExclusive, userId);
        }

        /// <summary>
        /// One employee's completed sales in the period. Backs both the manager's drill-down from the
        /// daily/shift report (V1.3) and the employee's own "مبيعاتي اليوم" screen, so the guard is
        /// self-or-admin: an employee can only ever pull their own invoices, never a colleague's.
        /// </summary>
        public IReadOnlyList<Sale> SalesIn(User actor, DateTime fromInclusive, DateTime toExclusive, int userId)
        {
            RequireSelfOrAdmin(actor, userId, "يمكن للموظف مراجعة مبيعاته الخاصة فقط.");
            return _sales.GetByDateRange(fromInclusive, toExclusive)
                         .Where(s => s.UserId == userId && s.Status != SaleStatus.Returned)
                         .OrderBy(s => s.CreatedAt)
                         .ToList();
        }

        /// <summary>The signed-in employee's own day: invoices, expenses and their totals — the whole
        /// payload of the self-service daily-sales screen in one guarded call.</summary>
        public EmployeeDaySheet MyDay(User actor, DateTime day)
            => DaySheet(actor, actor?.Id ?? 0, day.Date, day.Date.AddDays(1));

        /// <summary>One employee's invoices + expenses for a period, under the same self-or-admin guard.</summary>
        public EmployeeDaySheet DaySheet(User actor, int userId, DateTime fromInclusive, DateTime toExclusive)
        {
            if (actor == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            return new EmployeeDaySheet
            {
                Sales = SalesIn(actor, fromInclusive, toExclusive, userId),
                Expenses = ExpensesIn(actor, fromInclusive, toExclusive, userId),
                // The guard has already run inside SalesIn: self or admin. Attendance is that same
                // person's own clock-in, so it is read under the same permission.
                FirstLoginAt = _employees.GetAttendance(fromInclusive, toExclusive, userId)
                    .Select(a => (DateTime?)a.LoginAt).OrderBy(t => t).FirstOrDefault()
            };
        }

        // ---------------- Daily report (req 2) ----------------

        /// <summary>Per-employee daily report: sales, expenses and first clock-in for the given day.</summary>
        public IReadOnlyList<EmployeeDayRow> DailyReport(User admin, DateTime day)
            => RangeReport(admin, day.Date, day.Date.AddDays(1));

        /// <summary>
        /// Per-employee report over any period (V1.2 shift/weekly/monthly): first clock-in and
        /// attendance-day count, sales count/total, and money/medicine expenses per employee.
        /// </summary>
        public IReadOnlyList<EmployeeDayRow> RangeReport(User admin, DateTime fromInclusive, DateTime toExclusive)
        {
            Guard.RequireAdmin(admin, "تقرير الموظفين متاح للمدير فقط.");

            var users = _users.GetAll();
            // A partly returned invoice still counts for the employee, for what the customer kept.
            var sales = _sales.GetByDateRange(fromInclusive, toExclusive)
                              .Where(s => s.Status != SaleStatus.Returned).ToList();
            var attendance = _employees.GetAttendance(fromInclusive, toExclusive);
            var expenses = _employees.GetExpenses(fromInclusive, toExclusive);

            return users.Select(u => new EmployeeDayRow
            {
                User = u,
                FirstLoginAt = attendance.Where(a => a.UserId == u.Id)
                                         .OrderBy(a => a.LoginAt)
                                         .Select(a => (DateTime?)a.LoginAt).FirstOrDefault(),
                AttendanceDays = attendance.Where(a => a.UserId == u.Id)
                                           .Select(a => a.LoginAt.Date).Distinct().Count(),
                SalesCount = sales.Count(s => s.UserId == u.Id),
                SalesTotal = sales.Where(s => s.UserId == u.Id).Sum(s => s.NetTotal),
                MoneyExpenses = expenses.Where(e => e.UserId == u.Id && e.Type == ExpenseType.Money).Sum(e => e.Amount),
                MedicineExpenses = expenses.Where(e => e.UserId == u.Id && e.Type == ExpenseType.Medicine).Sum(e => e.Amount)
            })
            // show everyone who had any activity, plus all active employees
            .Where(r => r.User.IsActive || r.SalesCount > 0 || r.FirstLoginAt != null || r.TotalExpenses > 0)
            .OrderBy(r => r.User.Username)
            .ToList();
        }

        /// <summary>Sellable single units an item has right now — the ceiling the expense screen
        /// shows next to the picked medicine and the value the save check is made against.</summary>
        public int AvailableUnits(int itemId)
            => FefoAllocator.AvailableUnits(_stock.GetSellableBatches(itemId));

        private static void RequireSelfOrAdmin(User actor, int userId, string message = null)
        {
            if (actor == null || (!actor.IsAdmin && actor.Id != userId))
                throw new PermissionDeniedException(message ?? "يمكن للموظف تسجيل مصروفاته الخاصة فقط.");
        }
    }
}
