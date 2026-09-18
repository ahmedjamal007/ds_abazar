using System;
using System.Collections.Generic;
using Dawaii.Core.Services;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>Attendance, HR profile, leaves, deductions and expenses for the employee system (V1.2 req 2+5).</summary>
    public interface IEmployeeRepository
    {
        // Attendance
        int AddAttendance(AttendanceEntry entry);
        IReadOnlyList<AttendanceEntry> GetAttendance(DateTime fromInclusive, DateTime toExclusive, int? userId = null);

        // Profile (salary)
        EmployeeProfile GetProfile(int userId);
        void UpsertProfile(EmployeeProfile profile);

        // Leaves
        int AddLeave(LeaveEntry leave);
        IReadOnlyList<LeaveEntry> GetLeaves(int userId);
        void RemoveLeave(int leaveId);

        // Deductions
        int AddDeduction(DeductionEntry deduction);
        IReadOnlyList<DeductionEntry> GetDeductions(int userId, DateTime? fromInclusive = null, DateTime? toExclusive = null);
        void RemoveDeduction(int deductionId);

        // Expenses
        int AddExpense(EmployeeExpense expense);

        /// <summary>
        /// Writes a medicine expense AND takes the medicine off the shelf, in one transaction (V2.3).
        ///
        /// Medicine an employee takes physically leaves the pharmacy, so the expense row and the stock
        /// decrement are two halves of one event and must commit together — the same rule a sale
        /// follows. Each batch is decremented under a <c>quantity_units &gt;= n</c> guard, so a second
        /// terminal that got there first causes a clean rollback rather than negative stock, and a
        /// <c>stock_adjustments</c> row is written per batch so the shrinkage stays attributable.
        /// </summary>
        int AddMedicineExpense(EmployeeExpense expense, IReadOnlyList<BatchAllocation> allocations);
        IReadOnlyList<EmployeeExpense> GetExpenses(DateTime fromInclusive, DateTime toExclusive, int? userId = null);
    }
}
