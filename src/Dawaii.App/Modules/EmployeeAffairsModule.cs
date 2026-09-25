using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// Employee affairs (Admin, V1.2 req 2+5): the per-employee daily report (sales, expenses,
    /// clock-in) with printing, plus salary / deduction / leave / expense actions for the selected row.
    /// </summary>
    public class EmployeeAffairsModule : ModuleControl
    {
        private DateTimePicker _date;
        private DataGridView _grid;
        private Label _summary;
        private List<EmployeeDayRow> _rows = new List<EmployeeDayRow>();

        public EmployeeAffairsModule()
        {
            var title = Theme.PageHeader("شؤون الموظفين", "التقرير اليومي: المبيعات، الحضور، والمصروفات لكل موظف");

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            bar.Controls.Add(new Label { Text = "اليوم:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(11f) });
            _date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 140, Margin = new Padding(2, 10, 8, 0), Font = Theme.Base(12f) };
            _date.ValueChanged += (s, e) => Reload();
            bar.Controls.Add(_date);
            bar.Controls.Add(Theme.ActionButton("تحديث", Reload, width: 90));
            bar.Controls.Add(Theme.ActionButton("طباعة", PrintReport, primary: true, width: 90));
            bar.Controls.Add(Theme.ActionButton("Excel", () => ExportReport(pdf: false), width: 85));
            bar.Controls.Add(Theme.ActionButton("PDF", () => ExportReport(pdf: true), width: 80));

            var hr = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            hr.Controls.Add(new Label { Text = "للموظف المحدد:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted });
            hr.Controls.Add(Theme.ActionButton("تعيين راتب", SetSalary, width: 120));
            hr.Controls.Add(Theme.ActionButton("خصم", AddDeduction, width: 100));
            hr.Controls.Add(Theme.ActionButton("إجازة", AddLeave, width: 100));
            hr.Controls.Add(Theme.ActionButton("تسجيل مصروف", AddExpense, width: 130));
            hr.Controls.Add(Theme.ActionButton("كشف الراتب", ShowSalaryInfo, width: 120));

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f) };

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الموظف", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الحضور", DataPropertyName = "ClockIn", Width = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "عدد الفواتير", DataPropertyName = "Count", Width = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المبيعات", DataPropertyName = "Sales", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "مصروف نقدي", DataPropertyName = "Money", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "مصروف أدوية", DataPropertyName = "Meds", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "إجمالي المصروف", DataPropertyName = "Expenses", Width = 130 });

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(hr);
            Controls.Add(bar);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private void Reload()
        {
            try
            {
                _rows = Session.Services.Employees.DailyReport(Session.CurrentUser, _date.Value.Date).ToList();
                _grid.DataSource = _rows.Select(r => new
                {
                    Name = r.User.FullName ?? r.User.Username,
                    ClockIn = r.FirstLoginAt.HasValue ? r.FirstLoginAt.Value.ToString("HH:mm") : "—",
                    Count = r.SalesCount,
                    Sales = Fmt.Money(r.SalesTotal),
                    Money = Fmt.Money(r.MoneyExpenses),
                    Meds = Fmt.Money(r.MedicineExpenses),
                    Expenses = Fmt.Money(r.TotalExpenses)
                }).ToList();
                _summary.Text = $"إجمالي المبيعات: {Fmt.Money(_rows.Sum(r => r.SalesTotal))}   |   إجمالي المصروفات: {Fmt.Money(_rows.Sum(r => r.TotalExpenses))}";
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private EmployeeDayRow Selected()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _rows.Count) { Msg.Info("اختر موظفاً من القائمة."); return null; }
            return _rows[idx];
        }

        private static readonly string[] ReportHeaders =
            { "الموظف", "الحضور", "الفواتير", "المبيعات", "نقدي", "أدوية", "إجمالي المصروف" };

        private List<string[]> ReportRows() => _rows.Select(r => new[]
        {
            r.User.FullName ?? r.User.Username,
            r.FirstLoginAt?.ToString("HH:mm") ?? "—",
            r.SalesCount.ToString(),
            r.SalesTotal.ToString("0.00"),
            r.MoneyExpenses.ToString("0.00"),
            r.MedicineExpenses.ToString("0.00"),
            r.TotalExpenses.ToString("0.00")
        }).ToList();

        private void PrintReport()
        {
            if (_rows.Count == 0) { Msg.Info("لا توجد بيانات."); return; }
            ReportPrinter.Print(FindForm(), $"تقرير الموظفين اليومي — {_date.Value:yyyy-MM-dd}", ReportHeaders, ReportRows());
        }

        private void ExportReport(bool pdf)
        {
            if (_rows.Count == 0) { Msg.Info("لا توجد بيانات."); return; }
            string path = ReportExporter.SaveWithDialog(FindForm(),
                $"تقرير الموظفين اليومي — {_date.Value:yyyy-MM-dd}", ReportHeaders, ReportRows(), pdf);
            if (path != null) Msg.Info("تم حفظ التقرير:\n" + path);
        }

        private void SetSalary()
        {
            var row = Selected(); if (row == null) return;
            var current = Session.Services.Employees.GetProfile(row.User.Id);
            string s = Prompt.Show($"الراتب الشهري لـ {row.User.FullName ?? row.User.Username}", "تعيين راتب", current.MonthlySalary.ToString("0.##"));
            if (string.IsNullOrWhiteSpace(s)) return;
            if (!MoneyInput.TryParse(s, out decimal salary) || salary < 0) { Msg.Warn("قيمة غير صالحة."); return; }
            TryRun(() => Session.Services.Employees.SetSalary(Session.CurrentUser, row.User.Id, salary));
        }

        private void AddDeduction()
        {
            var row = Selected(); if (row == null) return;
            string s = Prompt.Show("مبلغ الخصم", "خصم");
            if (string.IsNullOrWhiteSpace(s)) return;
            if (!MoneyInput.TryParse(s, out decimal amount)) { Msg.Warn("قيمة غير صالحة."); return; }
            string reason = Prompt.Show("سبب الخصم (إلزامي)", "خصم");
            if (string.IsNullOrWhiteSpace(reason)) return;
            TryRun(() => Session.Services.Employees.AddDeduction(Session.CurrentUser, row.User.Id, amount, reason));
        }

        private void AddLeave()
        {
            var row = Selected(); if (row == null) return;
            string from = Prompt.Show("من تاريخ (YYYY-MM-DD)", "إجازة", DateTime.Today.ToString("yyyy-MM-dd"));
            if (string.IsNullOrWhiteSpace(from)) return;
            string to = Prompt.Show("إلى تاريخ (YYYY-MM-DD)", "إجازة", DateTime.Today.ToString("yyyy-MM-dd"));
            if (string.IsNullOrWhiteSpace(to)) return;
            if (!DateTime.TryParse(from, out DateTime f) || !DateTime.TryParse(to, out DateTime t)) { Msg.Warn("تاريخ غير صالح."); return; }
            string reason = Prompt.Show("السبب (اختياري)", "إجازة");
            TryRun(() => Session.Services.Employees.AddLeave(Session.CurrentUser, row.User.Id, f, t, reason));
        }

        private void AddExpense()
        {
            var row = Selected(); if (row == null) return;
            using (var f = new ExpenseForm(row.User.Id, row.User.FullName ?? row.User.Username))
                if (f.ShowDialog(FindForm()) == DialogResult.OK) Reload();
        }

        private void ShowSalaryInfo()
        {
            var row = Selected(); if (row == null) return;
            var profile = Session.Services.Employees.GetProfile(row.User.Id);
            decimal net = Session.Services.Employees.NetSalaryFor(row.User.Id, DateTime.Today.Year, DateTime.Today.Month);
            var deds = Session.Services.Employees.Deductions(row.User.Id).Take(5).ToList();
            var leaves = Session.Services.Employees.Leaves(row.User.Id).Take(5).ToList();
            string text =
                $"الموظف: {row.User.FullName ?? row.User.Username}\n" +
                $"الراتب الشهري: {Fmt.Money(profile.MonthlySalary)}\n" +
                $"صافي هذا الشهر (بعد الخصومات): {Fmt.Money(net)}\n\n" +
                "آخر الخصومات:\n" +
                (deds.Count == 0 ? "  — لا يوجد\n" : string.Join("\n", deds.Select(d => $"  {d.CreatedAt:yyyy-MM-dd}: {Fmt.Money(d.Amount)} — {d.Reason}")) + "\n") +
                "\nآخر الإجازات:\n" +
                (leaves.Count == 0 ? "  — لا يوجد" : string.Join("\n", leaves.Select(l => $"  {l.FromDate:yyyy-MM-dd} → {l.ToDate:yyyy-MM-dd} {l.Reason}")));
            Msg.Info(text, "كشف الراتب");
        }

        private void TryRun(Action a)
        {
            try { a(); Reload(); Msg.Info("تم الحفظ."); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }
    }
}
