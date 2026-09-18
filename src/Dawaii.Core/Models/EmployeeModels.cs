using System;

namespace Dawaii.Core.Models
{
    /// <summary>One login event — attendance is recorded automatically at sign-in (V1.2 req 2).</summary>
    public class AttendanceEntry
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public DateTime LoginAt { get; set; }
        public string Terminal { get; set; }
    }

    /// <summary>Per-employee HR data (monthly salary in SDG).</summary>
    public class EmployeeProfile
    {
        public int UserId { get; set; }
        public decimal MonthlySalary { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>A leave / vacation period.</summary>
    public class LeaveEntry
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public string Reason { get; set; }
        public int CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>A salary deduction.</summary>
    public class DeductionEntry
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public decimal Amount { get; set; }
        public string Reason { get; set; }
        public int CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public enum ExpenseType { Money, Medicine }

    /// <summary>Money taken or medicine dispensed by an employee (V1.2 req 5). Shows on the daily report.</summary>
    public class EmployeeExpense
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public ExpenseType Type { get; set; }

        /// <summary>Money value; for Medicine this is units × selling price at entry time.</summary>
        public decimal Amount { get; set; }

        public int? ItemId { get; set; }
        public int? Units { get; set; }
        public string Note { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Item name captured for display (not persisted).</summary>
        public string ItemName { get; set; }
    }
}
