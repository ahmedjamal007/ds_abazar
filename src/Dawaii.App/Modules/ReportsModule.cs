using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// Reports (FR-RPT-*, V1.2 req 1): daily / weekly / monthly time frames, period summary
    /// (sales, profit, cash/credit), items sold with quantities, stock and debt reports —
    /// every view printable and exportable to Excel (.xlsx) and PDF.
    /// </summary>
    public class ReportsModule : ModuleControl
    {
        private DateTimePicker _date;
        private ComboBox _period;
        private Label _summary;
        private DataGridView _grid;
        private string _viewTitle = "التقرير";
        private List<EmployeeDayRow> _employeeRows = new List<EmployeeDayRow>();
        private bool _showingEmployees;

        public ReportsModule()
        {
            var title = new Label { Text = "التقارير", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            top.Controls.Add(new Label { Text = "الفترة:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(11f) });
            _period = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2, 10, 8, 0), Font = Theme.Base(11f) };
            _period.Items.AddRange(new object[] { "يومي", "أسبوعي (آخر 7 أيام)", "شهري" });
            _period.SelectedIndex = 0;
            _period.SelectedIndexChanged += (s, e) => ShowSummary();
            top.Controls.Add(_period);
            top.Controls.Add(new Label { Text = "التاريخ:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(11f) });
            _date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 130, Margin = new Padding(2, 10, 8, 0), Font = Theme.Base(12f) };
            _date.ValueChanged += (s, e) => ShowSummary();
            top.Controls.Add(_date);
            top.Controls.Add(Theme.ActionButton("ملخص الفترة", ShowSummary, primary: true, width: 120));

            _summary = new Label { Dock = DockStyle.Top, Height = 66, Font = Theme.Base(12f), ForeColor = Theme.TextPrimary, BackColor = Theme.Surface, Padding = new Padding(12, 8, 12, 8), TextAlign = ContentAlignment.MiddleRight };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.Add(Theme.ActionButton("الموظفون", ShowEmployees, width: 100));
            buttons.Controls.Add(Theme.ActionButton("الأصناف المباعة", ShowItemsSold, width: 130));
            buttons.Controls.Add(Theme.ActionButton("المشتريات", ShowPurchases, width: 100));
            buttons.Controls.Add(Theme.ActionButton("مخزون منخفض", ShowLowStock, width: 120));
            buttons.Controls.Add(Theme.ActionButton("قرب الانتهاء", ShowNearExpiry, width: 115));
            buttons.Controls.Add(Theme.ActionButton("مخزون راكد", ShowDeadStock, width: 110));
            buttons.Controls.Add(Theme.ActionButton("ديون مستحقة", ShowDebts, width: 115));

            var export = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            export.Controls.Add(Theme.ActionButton("تصدير Excel", () => Export(pdf: false), width: 115));
            export.Controls.Add(Theme.ActionButton("تصدير PDF", () => Export(pdf: true), width: 110));
            export.Controls.Add(Theme.ActionButton("طباعة", PrintCurrent, width: 90));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_grid);
            _grid.CellDoubleClick += EmployeeDrillDown;

            Controls.Add(_grid);
            Controls.Add(export);
            Controls.Add(buttons);
            Controls.Add(_summary);
            Controls.Add(top);
            Controls.Add(title);
        }

        public override void OnActivated() => ShowSummary();

        // ---------- period ----------

        private (DateTime From, DateTime To, string Label) Period()
        {
            DateTime d = _date.Value.Date;
            switch (_period.SelectedIndex)
            {
                case 1: return (d.AddDays(-6), d.AddDays(1), $"أسبوعي {d.AddDays(-6):yyyy-MM-dd} → {d:yyyy-MM-dd}");
                case 2:
                    var first = new DateTime(d.Year, d.Month, 1);
                    return (first, first.AddMonths(1), $"شهري {first:yyyy-MM}");
                default: return (d, d.AddDays(1), $"يومي {d:yyyy-MM-dd}");
            }
        }

        // ---------- views ----------

        private void ShowSummary()
        {
            Guard(() =>
            {
                var (from, to, label) = Period();
                DailyReport r = Session.Services.Reports.Range(Session.CurrentUser, from, to);
                string profit = r.ProfitVisible ? $"  |  الأرباح: {Fmt.Money(r.TotalProfit)}" : "";
                _summary.Text =
                    $"{label}\n" +
                    $"عدد الفواتير: {r.TransactionCount}   |   إجمالي المبيعات: {Fmt.Money(r.TotalSales)}{profit}\n" +
                    $"نقدي: {Fmt.Money(r.CashTotal)}   |   آجل: {Fmt.Money(r.CreditTotal)}   |   مرتجعات: {r.ReturnedCount} ({Fmt.Money(r.ReturnedTotal)})";

                _viewTitle = "ملخص المبيعات — " + label;
                SetColumns(("البيان", 0), ("القيمة", 220));
                _grid.Rows.Clear();
                AddKv("عدد الفواتير", r.TransactionCount.ToString());
                AddKv("إجمالي المبيعات", r.TotalSales.ToString("0.00"));
                AddKv("مبيعات نقدية", r.CashTotal.ToString("0.00"));
                AddKv("مبيعات آجلة", r.CreditTotal.ToString("0.00"));
                if (r.ProfitVisible) AddKv("إجمالي الأرباح", r.TotalProfit.ToString("0.00"));
                AddKv("عدد المرتجعات", r.ReturnedCount.ToString());
                AddKv("قيمة المرتجعات", r.ReturnedTotal.ToString("0.00"));
            });
        }

        /// <summary>Items sold in the period with quantities and revenue (req 1: "Panadol: 10 units, total X").</summary>
        private void ShowItemsSold()
        {
            Guard(() =>
            {
                var (from, to, label) = Period();
                var rows = Session.Services.Reports.BestSellers(Session.CurrentUser, from, to, 500);
                _viewTitle = "الأصناف المباعة — " + label;
                SetColumns(("الصنف", 0), ("الكمية المباعة (حبة)", 180), ("الإيراد", 160));
                _grid.Rows.Clear();
                foreach (var r in rows) AddRow(r.Name, r.UnitsSold.ToString(), r.Revenue.ToString("0.00"));
            });
        }

        /// <summary>Purchases ("المشتريات") in the period: goods bought from suppliers/sellers, with the total (V1.4).</summary>
        private void ShowPurchases()
        {
            Guard(() =>
            {
                var (from, to, label) = Period();
                var rows = Session.Services.Purchases.InRange(from, to);
                _viewTitle = "المشتريات — " + label;
                SetColumns(("البيان", 0), ("المورد", 150), ("الكمية", 90), ("سعر الوحدة", 120), ("الإجمالي", 130), ("التاريخ", 130));
                _grid.Rows.Clear();
                foreach (var p in rows)
                    AddRow(p.Description, p.SupplierName ?? "", p.Quantity.ToString(),
                        p.UnitPrice.ToString("0.00"), p.Amount.ToString("0.00"), p.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
                _summary.Text = $"{label}   |   عدد المشتريات: {rows.Count}   |   إجمالي المشتريات: {Fmt.Money(rows.Sum(p => p.Amount))}";
            });
        }

        /// <summary>Per-employee report for the period; double-click a row to drill into that employee's sales (V1.3).</summary>
        private void ShowEmployees()
        {
            Guard(() =>
            {
                var (from, to, label) = Period();
                _employeeRows = Session.Services.Employees.RangeReport(Session.CurrentUser, from, to).ToList();
                _viewTitle = "تقرير الموظفين — " + label;
                bool daily = _period.SelectedIndex == 0;
                SetColumns(("الموظف", 0), (daily ? "أول حضور" : "أيام الحضور", 120), ("عدد الفواتير", 110),
                    ("المبيعات", 130), ("مصروف نقدي", 110), ("مصروف أدوية", 110), ("إجمالي المصروف", 130));
                _grid.Rows.Clear();
                foreach (var r in _employeeRows)
                    AddRow(r.User.FullName ?? r.User.Username,
                        daily ? (r.FirstLoginAt?.ToString("HH:mm") ?? "—") : r.AttendanceDays.ToString(),
                        r.SalesCount.ToString(), r.SalesTotal.ToString("0.00"),
                        r.MoneyExpenses.ToString("0.00"), r.MedicineExpenses.ToString("0.00"), r.TotalExpenses.ToString("0.00"));
                _summary.Text = "انقر مرتين على موظف لعرض تفاصيل مبيعاته.";
                _showingEmployees = true;
            });
        }

        private void EmployeeDrillDown(object sender, DataGridViewCellEventArgs e)
        {
            if (!_showingEmployees || e.RowIndex < 0 || e.RowIndex >= _employeeRows.Count) return;
            var (from, to, label) = Period();
            using (var f = new Dawaii.App.Forms.EmployeeSalesDetailForm(_employeeRows[e.RowIndex].User, from, to, label))
                f.ShowDialog(FindForm());
        }

        private void ShowDeadStock()
        {
            Guard(() =>
            {
                var rows = Session.Services.Reports.DeadStock(Session.CurrentUser, 60);
                _viewTitle = "مخزون راكد (بلا مبيعات 60 يوماً)";
                SetColumns(("الصنف", 0), ("المتاح (حبة)", 160));
                _grid.Rows.Clear();
                foreach (var r in rows) AddRow(r.Item.DisplayName, r.AvailableUnits.ToString());
            });
        }

        private void ShowLowStock()
        {
            Guard(() =>
            {
                var rows = Session.Services.Inventory.GetLowStock();
                _viewTitle = "مخزون منخفض";
                SetColumns(("الصنف", 0), ("المتاح (حبة)", 140), ("حد التنبيه", 140));
                _grid.Rows.Clear();
                foreach (var v in rows) AddRow(v.Item.DisplayName, v.AvailableUnits.ToString(), v.Item.MinQuantity.ToString());
            });
        }

        private void ShowNearExpiry()
        {
            Guard(() =>
            {
                var rows = Session.Services.Inventory.GetNearExpiry();
                _viewTitle = "قرب الانتهاء";
                SetColumns(("الصنف", 0), ("الكمية", 110), ("الصلاحية", 130), ("المتبقي (يوم)", 120));
                _grid.Rows.Clear();
                foreach (var r in rows)
                    AddRow(r.Item.DisplayName, r.Batch.QuantityUnits.ToString(), Fmt.Date(r.Batch.ExpiryDate),
                        r.IsExpired ? "منتهي" : r.DaysUntilExpiry.ToString());
            });
        }

        private void ShowDebts()
        {
            Guard(() =>
            {
                var rows = Session.Services.Debts.WithDebt();
                _viewTitle = "الديون المستحقة";
                SetColumns(("العميل", 0), ("الهاتف", 150), ("الرصيد", 150));
                _grid.Rows.Clear();
                foreach (var c in rows) AddRow(c.Name, c.Phone ?? "", c.Balance.ToString("0.00"));
                _summary.Text = $"إجمالي الديون المستحقة: {Fmt.Money(Session.Services.Debts.TotalOutstanding())}";
            });
        }

        // ---------- export / print (req 1) ----------

        private (string[] Headers, List<string[]> Rows) CaptureGrid()
        {
            var headers = _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray();
            var rows = _grid.Rows.Cast<DataGridViewRow>()
                .Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value) ?? "").ToArray())
                .ToList();
            return (headers, rows);
        }

        private void Export(bool pdf)
        {
            Guard(() =>
            {
                var (headers, rows) = CaptureGrid();
                if (rows.Count == 0) { Msg.Info("لا توجد بيانات للتصدير."); return; }
                string path = ReportExporter.SaveWithDialog(FindForm(), _viewTitle, headers, rows, pdf);
                if (path != null) Msg.Info("تم حفظ التقرير:\n" + path);
            });
        }

        private void PrintCurrent()
        {
            Guard(() =>
            {
                var (headers, rows) = CaptureGrid();
                if (rows.Count == 0) { Msg.Info("لا توجد بيانات للطباعة."); return; }
                ReportPrinter.Print(FindForm(), _viewTitle, headers, rows);
            });
        }

        // ---------- helpers ----------

        private void SetColumns(params (string header, int width)[] cols)
        {
            _showingEmployees = false;   // reset; ShowEmployees re-enables the drill-down after this
            _grid.Columns.Clear();
            foreach (var c in cols)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = c.header,
                    Width = c.width > 0 ? c.width : 100,
                    AutoSizeMode = c.width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
                });
        }

        private void AddKv(string k, string v) => AddRow(k, v);

        private void AddRow(params string[] values)
        {
            int i = _grid.Rows.Add();
            for (int c = 0; c < values.Length && c < _grid.Columns.Count; c++)
                _grid.Rows[i].Cells[c].Value = values[c];
        }

        private void Guard(Action a)
        {
            try { a(); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }
    }
}
