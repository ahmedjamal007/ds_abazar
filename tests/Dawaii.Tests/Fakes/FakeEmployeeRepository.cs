using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core;
using Dawaii.Core.Services;

namespace Dawaii.Tests.Fakes
{
    public class FakeEmployeeRepository : IEmployeeRepository
    {
        public readonly List<AttendanceEntry> Attendance = new List<AttendanceEntry>();
        public readonly List<EmployeeProfile> Profiles = new List<EmployeeProfile>();
        public readonly List<LeaveEntry> Leaves = new List<LeaveEntry>();
        public readonly List<DeductionEntry> Deductions = new List<DeductionEntry>();
        public readonly List<EmployeeExpense> Expenses = new List<EmployeeExpense>();
        private int _id = 1;

        /// <summary>The shelf a medicine expense takes from. Optional only so the older tests that never
        /// touch stock can keep constructing this with no arguments.</summary>
        private readonly FakeStockRepository _stock;

        public FakeEmployeeRepository(FakeStockRepository stock = null) { _stock = stock; }

        /// <summary>Set to make the attendance write fail, the way a locked database does.</summary>
        public bool AddAttendanceThrows;

        public int AddAttendance(AttendanceEntry entry)
        {
            if (AddAttendanceThrows) throw new InvalidOperationException("database is locked");
            entry.Id = _id++; Attendance.Add(entry); return entry.Id;
        }

        public IReadOnlyList<AttendanceEntry> GetAttendance(DateTime fromInclusive, DateTime toExclusive, int? userId = null)
            => Attendance.Where(a => a.LoginAt >= fromInclusive && a.LoginAt < toExclusive
                                     && (userId == null || a.UserId == userId)).ToList();

        public EmployeeProfile GetProfile(int userId) => Profiles.FirstOrDefault(p => p.UserId == userId);

        public void UpsertProfile(EmployeeProfile profile)
        {
            Profiles.RemoveAll(p => p.UserId == profile.UserId);
            Profiles.Add(profile);
        }

        public int AddLeave(LeaveEntry leave) { leave.Id = _id++; Leaves.Add(leave); return leave.Id; }
        public IReadOnlyList<LeaveEntry> GetLeaves(int userId) => Leaves.Where(l => l.UserId == userId).ToList();
        public void RemoveLeave(int leaveId) => Leaves.RemoveAll(l => l.Id == leaveId);

        public int AddDeduction(DeductionEntry deduction) { deduction.Id = _id++; Deductions.Add(deduction); return deduction.Id; }

        public IReadOnlyList<DeductionEntry> GetDeductions(int userId, DateTime? fromInclusive = null, DateTime? toExclusive = null)
            => Deductions.Where(d => d.UserId == userId
                                     && (fromInclusive == null || d.CreatedAt >= fromInclusive)
                                     && (toExclusive == null || d.CreatedAt < toExclusive)).ToList();

        public void RemoveDeduction(int deductionId) => Deductions.RemoveAll(d => d.Id == deductionId);

        public int AddExpense(EmployeeExpense expense) { expense.Id = _id++; Expenses.Add(expense); return expense.Id; }

        /// <summary>
        /// Records the expense AND takes the units off the fake shelf, because the real repository does
        /// (V2.3). A fake that only stored the row would let the whole class of "the medicine never left
        /// the shelf" bug pass here and fail against the database — which is exactly what happened.
        /// </summary>
        public int AddMedicineExpense(EmployeeExpense expense, IReadOnlyList<BatchAllocation> allocations)
        {
            if (allocations == null || allocations.Count == 0)
                throw new ValidationException("لا توجد كمية لخصمها من المخزون.");

            foreach (BatchAllocation a in allocations)
            {
                StockBatch batch = _stock?.GetBatch(a.BatchId);
                if (batch == null || batch.IsDisposed || batch.QuantityUnits < a.Units)
                    throw new InsufficientStockException(expense.ItemId ?? 0, a.Units, batch?.QuantityUnits ?? 0);
                _stock.SetBatchQuantity(a.BatchId, batch.QuantityUnits - a.Units);
                _stock.AddAdjustment(new StockAdjustment
                {
                    ItemId = expense.ItemId ?? 0, BatchId = a.BatchId, DeltaUnits = -a.Units,
                    Reason = "مصروف موظف (دواء)", UserId = expense.UserId
                });
            }

            return AddExpense(expense);
        }

        public IReadOnlyList<EmployeeExpense> GetExpenses(DateTime fromInclusive, DateTime toExclusive, int? userId = null)
            => Expenses.Where(e => e.CreatedAt >= fromInclusive && e.CreatedAt < toExclusive
                                   && (userId == null || e.UserId == userId)).ToList();
    }
}
