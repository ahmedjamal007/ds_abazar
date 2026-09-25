using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// End-of-shift report (تقرير الوردية): everything the manager reviews when the shift closes —
    /// sales summary + expected cash in drawer, per-employee attendance/sales/expenses, and items
    /// sold with quantities. Daily / weekly / monthly, printable and exportable to PDF, Excel and CSV.
    /// </summary>
    public class ShiftReportModule : ModuleControl
    {
        private ComboBox _period;
        private DateTimePicker _date;
        private KpiCard _kSales, _kCash, _kInvoices, _kExpenses;
        private DataGridView _grid;
        private Label _summary;
        private PillButton _viewEmployees, _viewItems, _viewExpenses, _viewPurchases;
        private List<EmployeeExpenseRow> _expenses = new List<EmployeeExpenseRow>();
        private List<Dawaii.Core.Models.Purchase> _purchases = new List<Dawaii.Core.Models.Purchase>();

        private DailyReport _totals;
        private List<EmployeeDayRow> _employees = new List<EmployeeDayRow>();
        private List<BestSellerRow> _items = new List<BestSellerRow>();
        private string _label = "";
        private DateTime _from, _to;
        private bool _showingEmployees = true;
        /// <summary>The shift added up. The arithmetic lives in Dawaii.Core so the screen and the
        /// printed sheet cannot drift apart, which they had (V2.4).</summary>
        private ShiftTill _till = new ShiftTill();

        /// <summary>Cash paid to medicine suppliers in the period — an outflow from the same drawer.</summary>

        public ShiftReportModule()
        {
            Padding = new Padding(20, 12, 20, 12);
            var title = Theme.PageHeader("تقرير الوردية", "ملخص نهاية اليوم: المبيعات، حضور الموظفين، والنقد المتوقع في الدرج");

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            bar.Controls.Add(new Label { Text = "الفترة:", AutoSize = true, Margin = new Padding(6, 15, 2, 0), Font = Theme.Base(11f) });
            _period = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2, 11, 8, 0), Font = Theme.Base(11f) };
            _period.Items.AddRange(new object[] { "يومي", "أسبوعي (آخر 7 أيام)", "شهري" });
            _period.SelectedIndex = 0;
            _period.SelectedIndexChanged += (s, e) => Reload();
            bar.Controls.Add(_period);
            bar.Controls.Add(new Label { Text = "التاريخ:", AutoSize = true, Margin = new Padding(6, 15, 2, 0), Font = Theme.Base(11f) });
            _date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 130, Margin = new Padding(2, 11, 8, 0), Font = Theme.Base(12f) };
            _date.ValueChanged += (s, e) => Reload();
            bar.Controls.Add(_date);
            bar.Controls.Add(Theme.ActionButton("طباعة", PrintReport, primary: true, width: 90));
            bar.Controls.Add(Theme.ActionButton("PDF", () => Export(ExportFormat.Pdf), width: 80));
            bar.Controls.Add(Theme.ActionButton("Excel", () => Export(ExportFormat.Xlsx), width: 85));
            bar.Controls.Add(Theme.ActionButton("CSV", () => Export(ExportFormat.Csv), width: 80));

            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 164, BackColor = Theme.Background,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 6, 0, 0)
            };
            _kSales = new KpiCard { Label = "إجمالي المبيعات", IconName = "cash", Accent = Theme.Primary, Filled = true, Margin = new Padding(0, 4, 0, 4) };
            _kCash = new KpiCard { Label = "النقد المتوقع في الدرج", IconName = "pos", Accent = Color.FromArgb(38, 108, 92), Margin = new Padding(0, 4, 0, 4) };
            _kInvoices = new KpiCard { Label = "عدد الفواتير", IconName = "receipt", Accent = Color.FromArgb(90, 100, 96), Margin = new Padding(0, 4, 0, 4) };
            _kExpenses = new KpiCard { Label = "مصروفات الموظفين", IconName = "cart", Accent = Color.FromArgb(200, 124, 12), Margin = new Padding(0, 4, 0, 4) };
            cards.Controls.AddRange(new Control[] { _kSales, _kCash, _kInvoices, _kExpenses });

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f) };

            var toggle = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            _viewEmployees = (PillButton)Theme.ActionButton("حضور ومبيعات الموظفين", ShowEmployees, primary: true, width: 190);
            _viewExpenses = (PillButton)Theme.ActionButton("المصروفات اليومية", ShowExpenses, width: 150);
            _viewPurchases = (PillButton)Theme.ActionButton("المشتريات", ShowPurchases, width: 110);
            _viewItems = (PillButton)Theme.ActionButton("الأصناف المباعة", ShowItems, width: 150);
            toggle.Controls.Add(_viewEmployees);
            toggle.Controls.Add(_viewExpenses);
            toggle.Controls.Add(_viewPurchases);
            toggle.Controls.Add(_viewItems);

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_grid);
            _grid.CellDoubleClick += EmployeeDrillDown;

            Controls.Add(_grid);
            Controls.Add(toggle);
            Controls.Add(_summary);
            Controls.Add(cards);
            Controls.Add(bar);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private (DateTime From, DateTime To) Period()
        {
            DateTime d = _date.Value.Date;
            switch (_period.SelectedIndex)
            {
                case 1: _label = $"أسبوعي {d.AddDays(-6):yyyy-MM-dd} → {d:yyyy-MM-dd}"; return (d.AddDays(-6), d.AddDays(1));
                case 2:
                    var first = new DateTime(d.Year, d.Month, 1);
                    _label = $"شهري {first:yyyy-MM}";
                    return (first, first.AddMonths(1));
                default: _label = $"يومي {d:yyyy-MM-dd}"; return (d, d.AddDays(1));
            }
        }

        private void Reload()
        {
            try
            {
                var (from, to) = Period();
                _from = from; _to = to;
                _totals = Session.Services.Reports.Range(Session.CurrentUser, from, to);
                _employees = Session.Services.Employees.RangeReport(Session.CurrentUser, from, to).ToList();
                _items = Session.Services.Reports.BestSellers(Session.CurrentUser, from, to, 500).ToList();

                var names = _employees.ToDictionary(r => r.User.Id, r => r.User.FullName ?? r.User.Username);
                _expenses = Session.Services.Employees.ExpensesIn(Session.CurrentUser, from, to).Select(ex => new EmployeeExpenseRow
                {
                    Employee = names.TryGetValue(ex.UserId, out var n) ? n : ("#" + ex.UserId),
                    Type = ex.Type == Dawaii.Core.Models.ExpenseType.Money ? "نقدي" : "دواء",
                    Note = !string.IsNullOrWhiteSpace(ex.Note) ? ex.Note
                           : (ex.Type == Dawaii.Core.Models.ExpenseType.Medicine ? $"كمية: {ex.Units} حبة" : ""),
                    Amount = ex.Amount,
                    At = ex.CreatedAt
                }).OrderBy(x => x.At).ToList();

                _purchases = Session.Services.Purchases.InRange(from, to).ToList();
                // Cash handed to medicine suppliers comes out of this same drawer (V2.3), and the
                // counter purchases do too. What each of those does to the expected cash is the
                // reconciliation's business, not this screen's.
                _till = ShiftReconciliation.Build(
                    Session.Services.Pos.SalesInRange(from, to),
                    _employees,
                    purchases: _purchases.Sum(p => p.Amount),
                    supplierCash: Session.Services.Suppliers.CashPaidToSuppliers(from, to));


                _kSales.Value = Fmt.Money(_totals.TotalSales);
                _kSales.Sub = _totals.ProfitVisible ? $"ربح {_totals.TotalProfit:0.00}" : "";
                _kCash.Value = Fmt.Money(_till.ExpectedCash);
                                _kInvoices.Value = _totals.TransactionCount.ToString();
                _kInvoices.Sub = _totals.ReturnedCount > 0 ? $"{_totals.ReturnedCount} مرتجع" : "";
                _kExpenses.Value = Fmt.Money(_till.AllExpenses);
                _kExpenses.Sub = $"نقدي {_till.MoneyExpenses:0.00}";
                _kCash.Sub = $"كاش {_till.Cash:0.00}" +
                             (_till.SupplierCash > 0m ? $" − موردون {_till.SupplierCash:0.00}" : "");

                _summary.Text = $"{_label}   |   كاش: {Fmt.Money(_till.Cash)}   |   بنكك: {Fmt.Money(_till.Bankak)}   |   فوري: {Fmt.Money(_till.Fawry)}   |   أوكاش: {Fmt.Money(_till.Ocash)}" +
                                $"   |   آجل: {Fmt.Money(_totals.CreditTotal)}   |   مشتريات: {Fmt.Money(_till.Purchases)}" +
                                $"   |   سداد موردين (نقداً): {Fmt.Money(_till.SupplierCash)}" +
                                $"   |   مرتجعات: {_totals.ReturnedCount} ({Fmt.Money(_totals.ReturnedTotal)})";

                foreach (Control c in Controls) c.Invalidate();
                ShowEmployees();
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        // ---------------- grid views ----------------

        private void EmployeeDrillDown(object sender, DataGridViewCellEventArgs e)
        {
            if (!_showingEmployees || e.RowIndex < 0 || e.RowIndex >= _employees.Count) return;
            var row = _employees[e.RowIndex];
            using (var f = new EmployeeSalesDetailForm(row.User, _from, _to, _label))
                f.ShowDialog(FindForm());
        }

        private void ShowExpenses()
        {
            _showingEmployees = false;
            SetActive(_viewExpenses);
            SetColumns(("الموظف", 0), ("النوع", 90), ("الملاحظة", 230), ("القيمة", 110), ("الوقت", 130));
            _grid.Rows.Clear();
            foreach (var r in _expenses)
                AddRow(r.Employee, r.Type, r.Note, r.Amount.ToString("0.00"), r.At.ToString("yyyy-MM-dd HH:mm"));
            if (_expenses.Count == 0) _summary.Text = "لا توجد مصروفات في هذه الفترة.";
        }

        private void ShowPurchases()
        {
            _showingEmployees = false;
            SetActive(_viewPurchases);
            SetColumns(("البيان", 0), ("المورد", 140), ("الكمية", 80), ("سعر الوحدة", 110), ("الإجمالي", 120), ("الوقت", 130));
            _grid.Rows.Clear();
            foreach (var p in _purchases)
                AddRow(p.Description, p.SupplierName ?? "", p.Quantity.ToString(),
                    p.UnitPrice.ToString("0.00"), p.Amount.ToString("0.00"), p.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
            if (_purchases.Count == 0) _summary.Text = "لا توجد مشتريات في هذه الفترة.";
        }

        private void ShowEmployees()
        {
            _showingEmployees = true;
            SetActive(_viewEmployees);
            bool daily = _period.SelectedIndex == 0;
            SetColumns(("الموظف", 0), (daily ? "الحضور" : "أيام الحضور", 110), ("عدد الفواتير", 110),
                ("المبيعات", 120), ("مصروف نقدي", 110), ("مصروف أدوية", 110), ("إجمالي المصروف", 130));
            _grid.Rows.Clear();
            foreach (var r in _employees)
                AddRow(r.User.FullName ?? r.User.Username,
                    daily ? (r.FirstLoginAt?.ToString("HH:mm") ?? "—") : r.AttendanceDays.ToString(),
                    r.SalesCount.ToString(), r.SalesTotal.ToString("0.00"),
                    r.MoneyExpenses.ToString("0.00"), r.MedicineExpenses.ToString("0.00"), r.TotalExpenses.ToString("0.00"));
        }

        private void ShowItems()
        {
            _showingEmployees = false;
            SetActive(_viewItems);
            SetColumns(("الصنف", 0), ("الكمية المباعة (حبة)", 190), ("الإيراد", 160));
            _grid.Rows.Clear();
            foreach (var r in _items)
                AddRow(r.Name, r.UnitsSold.ToString(), r.Revenue.ToString("0.00"));
        }

        private void SetActive(PillButton active)
        {
            foreach (var b in new[] { _viewEmployees, _viewExpenses, _viewPurchases, _viewItems })
            {
                b.Outline = b != active;
                b.Invalidate();
            }
        }

        // ---------------- print / export (full multi-section report) ----------------

        private string ReportTitle => "تقرير الوردية — " + _label;

        private List<ReportSection> BuildSections()
        {
            bool daily = _period.SelectedIndex == 0;

            var summaryRows = new List<string[]>
            {
                new[] { "عدد الفواتير", _totals.TransactionCount.ToString() },
                new[] { "إجمالي المبيعات", _totals.TotalSales.ToString("0.00") },
                new[] { "كاش", _till.Cash.ToString("0.00") },
                new[] { "بنكك", _till.Bankak.ToString("0.00") },
                new[] { "فوري", _till.Fawry.ToString("0.00") },
                new[] { "مبيعات آجلة", _totals.CreditTotal.ToString("0.00") },
                new[] { "مرتجعات", $"{_totals.ReturnedCount} ({_totals.ReturnedTotal:0.00})" },
                new[] { "مصروفات الموظفين (نقدي)", _till.MoneyExpenses.ToString("0.00") },
                new[] { "مصروفات الموظفين (إجمالي)", _till.AllExpenses.ToString("0.00") },
                new[] { "المشتريات", _till.Purchases.ToString("0.00") },
                new[] { "أوكاش", _till.Ocash.ToString("0.00") },
                new[] { "سداد موردين (نقداً)", _till.SupplierCash.ToString("0.00") },
                // Was written out by hand here and left supplier cash out, so the paper and
                // the screen disagreed by exactly what had been paid at the door (V2.4).
                new[] { "النقد المتوقع في الدرج", _till.ExpectedCash.ToString("0.00") }
            };
            if (_totals.ProfitVisible)
                summaryRows.Insert(2, new[] { "إجمالي الأرباح", _totals.TotalProfit.ToString("0.00") });

            var employeeRows = _employees.Select(r => new[]
            {
                r.User.FullName ?? r.User.Username,
                daily ? (r.FirstLoginAt?.ToString("HH:mm") ?? "—") : r.AttendanceDays.ToString(),
                r.SalesCount.ToString(), r.SalesTotal.ToString("0.00"),
                r.MoneyExpenses.ToString("0.00"), r.MedicineExpenses.ToString("0.00"), r.TotalExpenses.ToString("0.00")
            }).Cast<string[]>().ToList();

            var itemRows = _items.Select(r => new[] { r.Name, r.UnitsSold.ToString(), r.Revenue.ToString("0.00") })
                                 .Cast<string[]>().ToList();

            var expenseRows = _expenses.Select(r => new[]
            {
                r.Employee, r.Type, r.Note, r.Amount.ToString("0.00"), r.At.ToString("yyyy-MM-dd HH:mm")
            }).Cast<string[]>().ToList();

            var purchaseRows = _purchases.Select(p => new[]
            {
                p.Description, p.SupplierName ?? "", p.Quantity.ToString(),
                p.UnitPrice.ToString("0.00"), p.Amount.ToString("0.00"), p.CreatedAt.ToString("yyyy-MM-dd HH:mm")
            }).Cast<string[]>().ToList();

            return new List<ReportSection>
            {
                new ReportSection("ملخص المبيعات", new[] { "البيان", "القيمة" }, summaryRows),
                new ReportSection("حضور ومبيعات الموظفين",
                    new[] { "الموظف", daily ? "الحضور" : "أيام الحضور", "الفواتير", "المبيعات", "نقدي", "أدوية", "إجمالي المصروف" },
                    employeeRows),
                new ReportSection("المصروفات اليومية",
                    new[] { "الموظف", "النوع", "الملاحظة", "القيمة", "الوقت" }, expenseRows),
                new ReportSection("المشتريات",
                    new[] { "البيان", "المورد", "الكمية", "سعر الوحدة", "الإجمالي", "الوقت" }, purchaseRows),
                new ReportSection("الأصناف المباعة", new[] { "الصنف", "الكمية (حبة)", "الإيراد" }, itemRows)
            };
        }

        private void PrintReport()
        {
            if (_totals == null) { Msg.Info("لا توجد بيانات."); return; }
            ReportPrinter.PrintSections(FindForm(), ReportTitle, BuildSections());
        }

        private void Export(ExportFormat format)
        {
            if (_totals == null) { Msg.Info("لا توجد بيانات."); return; }
            try
            {
                string path = ReportExporter.SaveSectionsWithDialog(FindForm(), ReportTitle, BuildSections(), format);
                if (path != null) Msg.Info("تم حفظ التقرير:\n" + path);
            }
            catch (Exception ex) { Msg.Error("تعذّر التصدير: " + ex.Message); }
        }

        // ---------------- helpers ----------------

        private void SetColumns(params (string header, int width)[] cols)
        {
            _grid.Columns.Clear();
            foreach (var c in cols)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = c.header,
                    Width = c.width > 0 ? c.width : 100,
                    AutoSizeMode = c.width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
                });
        }

        private void AddRow(params string[] values)
        {
            int i = _grid.Rows.Add();
            for (int c = 0; c < values.Length && c < _grid.Columns.Count; c++)
                _grid.Rows[i].Cells[c].Value = values[c];
        }
    }

    /// <summary>A single staff expense row (with its note) for the shift report's daily-expenses view.</summary>
    internal class EmployeeExpenseRow
    {
        public string Employee { get; set; }
        public string Type { get; set; }
        public string Note { get; set; }
        public decimal Amount { get; set; }
        public DateTime At { get; set; }
    }
}
