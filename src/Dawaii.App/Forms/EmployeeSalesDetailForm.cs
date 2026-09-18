using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// One employee's individual invoices and logged expenses for a period. Two ways in:
    /// a manager double-clicking an employee on the daily/shift report (V1.3), and an employee
    /// opening their own day from "مبيعاتي اليوم" via <see cref="ForCurrentUser"/>.
    /// Either way the rows come from <c>EmployeeService.DaySheet</c>, which refuses any user id
    /// but the caller's own unless the caller is an admin.
    /// Selecting an invoice shows what it actually contained underneath it (V1.9): the list used to
    /// give a total per receipt and nothing else, so there was no way to see what had been sold.
    /// </summary>
    public class EmployeeSalesDetailForm : BaseForm
    {
        private readonly User _user;
        private readonly DateTime _from, _to;
        private readonly string _label;
        private readonly string _titleOverride;

        public EmployeeSalesDetailForm(User user, DateTime from, DateTime to, string label, string titleOverride = null)
        {
            _user = user;
            _from = from;
            _to = to;
            _label = label;
            _titleOverride = titleOverride;
            BuildUi();
            LoadData();
        }

        /// <summary>The signed-in employee's own sales for today. The user is taken from the session,
        /// never from a grid row, so this screen cannot be pointed at a colleague.</summary>
        public static EmployeeSalesDetailForm ForCurrentUser()
        {
            User me = Session.CurrentUser;
            return new EmployeeSalesDetailForm(me, DateTime.Today, DateTime.Today.AddDays(1),
                $"يومي {DateTime.Today:yyyy-MM-dd}", "مبيعاتي اليوم");
        }

        private DataGridView _sales, _expenses;
        private ReceiptDetailView _receipt;
        private Label _summary;

        /// <summary>Invoice ids by grid row, so the detail can be loaded for whichever row is selected
        /// (the id is not a displayed column — the receipt shows the invoice NUMBER).</summary>
        private readonly List<int> _saleIds = new List<int>();

        /// <summary>True while the grid is being filled, so half-built rows don't drive the detail pane.</summary>
        private bool _loading;

        private void BuildUi()
        {
            Text = _titleOverride ?? $"مبيعات الموظف — {_user.FullName ?? _user.Username}";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(820, 760);
            BackColor = Theme.Background;

            var title = new Label
            {
                Text = $"{_user.FullName ?? _user.Username}  —  {_label}",
                Font = Theme.Title(15f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 42,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 12, 0)
            };
            _summary = new Label
            {
                Dock = DockStyle.Top, Height = 46, Font = Theme.Base(11.5f), ForeColor = Theme.TextPrimary,
                BackColor = Theme.Surface, Padding = new Padding(12, 6, 12, 6), TextAlign = ContentAlignment.MiddleRight
            };

            var salesLabel = new Label { Text = "الفواتير", Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.TextPrimary, Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 12, 0) };
            _sales = new DataGridView
            {
                Dock = DockStyle.Top, Height = 220, AutoGenerateColumns = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
            };
            Theme.StyleGrid(_sales);
            AddCol(_sales, "رقم الفاتورة", 150);
            AddCol(_sales, "الوقت", 150);
            AddCol(_sales, "النوع", 120);
            AddCol(_sales, "طريقة الدفع", 120);
            AddCol(_sales, "الإجمالي", 0);
            _sales.SelectionChanged += (s, e) => ShowSelectedReceipt();

            _receipt = new ReceiptDetailView { Dock = DockStyle.Top, Height = 210 };

            var expLabel = new Label { Text = "المصروفات", Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.TextPrimary, Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 12, 0) };
            _expenses = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_expenses);
            AddCol(_expenses, "النوع", 120);
            AddCol(_expenses, "التفاصيل", 0);
            AddCol(_expenses, "القيمة", 140);

            // The day's summary goes out on the receipt printer — the one the employee is standing
            // beside at the end of a shift — as "تقرير مبيعات يومي" (V2.3). Preview first, or straight
            // to paper.
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 0) };
            bottom.Controls.Add(Theme.ActionButton("طباعة ملخص اليوم", PrintSummary, primary: true, width: 170));
            bottom.Controls.Add(Theme.ActionButton("معاينة الملخص", PreviewSummary, width: 150));
            bottom.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 120));

            // Docking resolves last-added outwards, so this order reads bottom-up: the expenses grid is
            // added first to take whatever space the fixed-height bands above it leave.
            Controls.Add(_expenses);
            Controls.Add(expLabel);
            Controls.Add(_receipt);
            Controls.Add(_sales);
            Controls.Add(salesLabel);
            Controls.Add(_summary);
            Controls.Add(title);
            Controls.Add(bottom);
        }

        /// <summary>The sheet as last loaded — what the summary is printed from.</summary>
        private Dawaii.Core.Services.EmployeeDaySheet _sheet;

        private Dawaii.App.Printing.ReceiptDocument BuildSummary()
        {
            if (_sheet == null) throw new InvalidOperationException("لم تُحمَّل بيانات اليوم بعد.");
            var info = Session.Services.CreateReceiptInfo(_user.FullName ?? _user.Username);
            return Dawaii.App.Printing.DailySalesReceiptBuilder.Build(_sheet, _user, _from, _to, info);
        }

        private void PreviewSummary()
        {
            try
            {
                Dawaii.App.Printing.ThermalReceipt.ShowPreview(this, BuildSummary(),
                    Session.Services.CreateReceiptInfo(_user.FullName ?? _user.Username), "تقرير مبيعات يومي");
            }
            catch (Exception ex) { Msg.Error("تعذّرت المعاينة: " + ex.Message); }
        }

        private void PrintSummary()
        {
            try
            {
                Dawaii.App.Printing.ThermalReceipt.Print(this, BuildSummary(),
                    Session.Services.CreateReceiptInfo(_user.FullName ?? _user.Username), "تقرير مبيعات يومي");
            }
            catch (Exception ex) { Log.Error("Daily summary print", ex); Msg.Warn("تعذّرت الطباعة: " + ex.Message); }
        }

        private void LoadData()
        {
            // Adding the first row makes it current at once, which raises SelectionChanged before the
            // id list has caught up. Detail loading is held off until the grid is fully built and then
            // run once, explicitly — waiting on the event would also miss the case where row 0 is
            // already current and assigning it again changes nothing.
            _loading = true;
            try
            {
                var sheet = Session.Services.Employees.DaySheet(Session.CurrentUser, _user.Id, _from, _to);
                _sheet = sheet;
                _saleIds.Clear();
                foreach (Sale s in sheet.Sales)
                {
                    int i = _sales.Rows.Add();
                    _saleIds.Add(s.Id);
                    _sales.Rows[i].Cells[0].Value = s.SaleNumber;
                    _sales.Rows[i].Cells[1].Value = s.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                    _sales.Rows[i].Cells[2].Value = s.SaleType == SaleType.Credit ? "آجل" : "نقدي";
                    _sales.Rows[i].Cells[3].Value = s.SaleType == SaleType.Credit ? "—" : PaymentLabel(s.PaymentMethod);
                    _sales.Rows[i].Cells[4].Value = Fmt.Money(s.Total);
                }

                foreach (EmployeeExpense ex in sheet.Expenses)
                {
                    int i = _expenses.Rows.Add();
                    _expenses.Rows[i].Cells[0].Value = ex.Type == ExpenseType.Money ? "نقدي" : "دواء";
                    _expenses.Rows[i].Cells[1].Value = ex.Note ?? "";
                    _expenses.Rows[i].Cells[2].Value = Fmt.Money(ex.Amount);
                }

                _summary.Text = $"الحضور: {(sheet.FirstLoginAt.HasValue ? sheet.FirstLoginAt.Value.ToString("HH:mm") : "—")}   |   " +
                                $"عدد الفواتير: {sheet.SalesCount}   |   إجمالي المبيعات: {Fmt.Money(sheet.SalesTotal)}   |   " +
                                $"عدد المصروفات: {sheet.ExpensesCount}   |   إجمالي المصروفات: {Fmt.Money(sheet.ExpensesTotal)}";

                // Open on the first invoice so the screen never starts on an empty detail pane.
                if (_sales.Rows.Count > 0) _sales.CurrentCell = _sales.Rows[0].Cells[0];
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
            finally { _loading = false; }

            ShowSelectedReceipt();
        }

        /// <summary>
        /// Loads the selected invoice's lines. The day sheet lists invoices without their lines (one
        /// query for the whole period), so the detail is fetched per selection — one small read, and
        /// only for the receipt actually being looked at.
        /// </summary>
        private void ShowSelectedReceipt()
        {
            if (_loading) return;
            if (_saleIds.Count == 0) { _receipt.ShowMessage("لا توجد فواتير في هذه الفترة."); return; }

            int row = _sales.CurrentRow?.Index ?? -1;
            if (row < 0 || row >= _saleIds.Count) { _receipt.ShowMessage("اختر فاتورة لعرض أصنافها."); return; }
            try { _receipt.ShowSale(Session.Services.Pos.GetSale(_saleIds[row])); }
            catch (Exception ex) { _receipt.ShowMessage("تعذّر تحميل الفاتورة: " + ex.Message); }
        }

        private static string PaymentLabel(string method) => PaymentMethods.LabelAr(method);

        private static void AddCol(DataGridView grid, string header, int width)
            => grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Width = width > 0 ? width : 120,
                AutoSizeMode = width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
            });
    }
}
