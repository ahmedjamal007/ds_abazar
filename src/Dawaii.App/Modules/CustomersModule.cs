using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using System.Collections.Generic;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// Customers &amp; debt ledger (FR-DBT-*). The manager owns the account holders themselves; a
    /// "موظف ذو امتيازات" reaches this screen for the two jobs a customer walks in with — reading a
    /// statement and paying off a balance — so the buttons that change the customer record are hidden
    /// from them (the service refuses those anyway). A new customer is still opened at the till.
    /// </summary>
    public class CustomersModule : ModuleControl
    {
        private TextBox _search;
        private CheckBox _onlyDebt;
        private DataGridView _grid;
        private Label _summary;
        private List<Customer> _rows = new List<Customer>();

        public CustomersModule()
        {
            var title = new Label { Text = "العملاء والديون", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _search.TextChanged += (s, e) => Reload();

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            // Opening, editing and deleting the ledger's account holders is the manager's, so anyone
            // else is left with the statement — and the payment taken from it — and is not shown
            // buttons the service would refuse anyway.
            if (Session.IsAdmin) toolbar.Controls.Add(Btn("عميل جديد", AddCustomer));
            toolbar.Controls.Add(Btn("كشف حساب / دفعة", OpenStatement));
            if (Session.IsAdmin)
            {
                toolbar.Controls.Add(Btn("تعديل العميل", EditCustomer));
                toolbar.Controls.Add(Btn("حذف العميل", DeleteCustomer));
            }
            _onlyDebt = new CheckBox { Text = "أصحاب الديون فقط", AutoSize = true, Margin = new Padding(10, 14, 6, 0), Font = Theme.Base(11f) };
            _onlyDebt.CheckedChanged += (s, e) => Reload();
            toolbar.Controls.Add(_onlyDebt);
            toolbar.Controls.Add(Btn("تحديث", Reload));

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(11f) };

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الاسم", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الهاتف", DataPropertyName = "Phone", Width = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الرصيد", DataPropertyName = "Balance", Width = 140 });
            _grid.CellDoubleClick += (s, e) => OpenStatement();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _rows[e.RowIndex].Balance > 0)
                    e.CellStyle.ForeColor = Theme.Danger;
            };

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(toolbar);
            Controls.Add(_search);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private void Reload()
        {
            try
            {
                _rows = (_onlyDebt.Checked
                    ? Session.Services.Debts.WithDebt()
                    : Session.Services.Debts.Search(_search.Text)).ToList();
                if (_onlyDebt.Checked && !string.IsNullOrWhiteSpace(_search.Text))
                    _rows = _rows.Where(c => (c.Name ?? "").Contains(_search.Text)).ToList();

                _grid.DataSource = _rows.Select(c => new { c.Id, c.Name, Phone = c.Phone ?? "", Balance = Fmt.Money(c.Balance) }).ToList();
                _summary.Text = $"إجمالي الديون المستحقة: {Fmt.Money(Session.Services.Debts.TotalOutstanding())}";
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private int? SelectedId() => Selected()?.Id;

        private void AddCustomer()
        {
            string[] entered = Prompt.ShowTwo("اسم العميل", "رقم الهاتف (اختياري)", "عميل جديد");
            if (entered == null || string.IsNullOrWhiteSpace(entered[0])) return;
            try { Session.Services.Debts.CreateCustomer(Session.CurrentUser, entered[0], entered[1]); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void EditCustomer()
        {
            Customer c = Selected();
            if (c == null) { Msg.Info("اختر عميلاً."); return; }

            string[] entered = Prompt.ShowTwo("اسم العميل", "رقم الهاتف (اختياري)", "تعديل العميل", c.Name, c.Phone ?? "");
            if (entered == null) return;
            try { Session.Services.Debts.UpdateCustomer(Session.CurrentUser, c.Id, entered[0], entered[1]); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        /// <summary>
        /// Deletes a customer. One who has bought or owed anything is kept — the invoices and the ledger
        /// name them — and a customer still carrying a balance is refused by the service outright, since
        /// deleting the debtor is not how a debt gets settled.
        /// </summary>
        private void DeleteCustomer()
        {
            Customer c = Selected();
            if (c == null) { Msg.Info("اختر عميلاً."); return; }
            if (!Msg.Confirm($"حذف العميل \"{c.Name}\" نهائياً؟ لا يمكن التراجع.")) return;

            try
            {
                if (Session.Services.Debts.DeleteCustomer(Session.CurrentUser, c.Id)) Msg.Info("تم حذف العميل.");
                else Msg.Warn("لا يمكن حذف هذا العميل لوجود فواتير أو حركات حساب مسجلة باسمه.");
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        /// <summary>The customer behind the selected row. The Id is not a displayed column, so the row
        /// index is mapped back into the list the grid was built from.</summary>
        private Customer Selected()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
        }

        private void OpenStatement()
        {
            int? id = SelectedId();
            if (id == null) { Msg.Info("اختر عميلاً."); return; }
            using (var f = new StatementForm(id.Value)) f.ShowDialog(FindForm());
            Reload();
        }

        private static Button Btn(string text, Action onClick)
            => Theme.ActionButton(text, onClick);
    }
}
