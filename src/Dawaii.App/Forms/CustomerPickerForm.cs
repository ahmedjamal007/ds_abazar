using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>Search and select a customer (for credit sales / debt payments); can add a new one.</summary>
    public class CustomerPickerForm : BaseForm
    {
        private TextBox _search;
        private DataGridView _grid;
        private List<Customer> _rows = new List<Customer>();

        public Customer Selected { get; private set; }

        public CustomerPickerForm()
        {
            Text = "اختيار عميل";
            ClientSize = new Size(520, 460);
            StartPosition = FormStartPosition.CenterParent;

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _search.TextChanged += (s, e) => Reload();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.Add(Btn("اختيار", Choose, primary: true));
            bar.Controls.Add(Btn("عميل جديد", AddNew));
            bar.Controls.Add(Btn("إلغاء", () => { DialogResult = DialogResult.Cancel; Close(); }));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الاسم", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الهاتف", DataPropertyName = "Phone", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الرصيد", DataPropertyName = "Balance", Width = 120 });
            _grid.CellDoubleClick += (s, e) => Choose();

            Controls.Add(_grid);
            Controls.Add(_search);
            Controls.Add(bar);
            Reload();
            ActiveControl = _search;
        }

        private void Reload()
        {
            _rows = Session.Services.Customers.Search(_search.Text, 100).ToList();
            _grid.DataSource = _rows.Select(c => new { c.Id, c.Name, Phone = c.Phone ?? "", Balance = Fmt.Money(c.Balance) }).ToList();
        }

        private Customer Current()
        {
            // The Id is not a displayed column; map the selected row back by index into _rows.
            int idx = _grid.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
        }

        private void Choose()
        {
            Selected = Current();
            if (Selected == null) { Msg.Info("اختر عميلاً."); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void AddNew()
        {
            string name = Prompt.Show("اسم العميل", "عميل جديد");
            if (string.IsNullOrWhiteSpace(name)) return;
            string phone = Prompt.Show("رقم الهاتف (اختياري)", "عميل جديد");
            int id = Session.Services.Customers.Add(new Customer { Name = name.Trim(), Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim() });
            Reload();
            Selected = Session.Services.Customers.GetById(id);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Button Btn(string text, Action onClick, bool primary = false)
            => Theme.ActionButton(text, onClick, primary, width: 130);
    }
}
