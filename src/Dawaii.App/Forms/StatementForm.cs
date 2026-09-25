using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Services;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// Per-customer statement + record payment / charge (FR-DBT-03/04).
    /// A debt line used to show only its amount; selecting one now shows the invoice behind it (V1.9),
    /// so the customer asking "what is this 340 for?" can be answered from this screen. Payments and
    /// manually-added debts have no invoice, and say so.
    /// </summary>
    public class StatementForm : BaseForm
    {
        private readonly int _customerId;
        private Label _header;
        private DataGridView _grid;
        private ReceiptDetailView _receipt;

        /// <summary>Invoice id per statement row (null for payments and manual charges).</summary>
        private readonly List<int?> _saleIds = new List<int?>();

        public StatementForm(int customerId)
        {
            _customerId = customerId;
            Text = "كشف حساب العميل";
            ClientSize = new Size(720, 680);
            StartPosition = FormStartPosition.CenterParent;

            _header = new Label { Dock = DockStyle.Top, Height = 44, Font = Theme.Base(13f, FontStyle.Bold), ForeColor = Theme.Primary, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
            };
            Theme.StyleGrid(_grid);
            _grid.SelectionChanged += (s, e) => ShowSelectedReceipt();
            _receipt = new ReceiptDetailView { Dock = DockStyle.Bottom, Height = 240 };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "التاريخ", DataPropertyName = "Date", Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "النوع", DataPropertyName = "Type", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المبلغ", DataPropertyName = "Amount", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الرصيد", DataPropertyName = "Balance", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ملاحظة", DataPropertyName = "Note", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.Add(Btn("تسجيل دفعة", RecordPayment, primary: true));
            if (Session.IsAdmin) bar.Controls.Add(Btn("إضافة دين", AddCharge));
            bar.Controls.Add(Btn("إغلاق", () => Close()));

            Controls.Add(_grid);
            Controls.Add(_receipt);
            Controls.Add(_header);
            Controls.Add(bar);
            Reload();
        }

        private void Reload()
        {
            Customer c = Session.Services.Debts.Get(_customerId);
            _header.Text = c == null ? "" : $"{c.Name}  —  الرصيد: {Fmt.Money(c.Balance)}";
            var rows = Session.Services.Debts.GetStatement(_customerId);
            _saleIds.Clear();
            _saleIds.AddRange(rows.Select(r => r.Transaction.SaleId));
            _grid.DataSource = rows.Select(r => new
            {
                Date = Fmt.DateTime(r.Transaction.CreatedAt),
                Type = r.Transaction.Type == DebtTransactionType.Charge ? "دين" : "دفعة",
                Amount = Fmt.Money(r.Transaction.Amount),
                Balance = Fmt.Money(r.RunningBalance),
                Note = r.Transaction.Note ?? ""
            }).ToList();
            ShowSelectedReceipt();
        }

        /// <summary>Shows the invoice a debt line came from. Only a charge raised by a credit sale has
        /// one — a payment, or a debt the manager typed in by hand, has nothing to itemise.</summary>
        private void ShowSelectedReceipt()
        {
            int row = _grid.CurrentRow?.Index ?? -1;
            if (row < 0 || row >= _saleIds.Count) { _receipt.ShowMessage("اختر حركة لعرض أصنافها."); return; }

            int? saleId = _saleIds[row];
            if (!saleId.HasValue) { _receipt.ShowMessage("هذه الحركة ليست فاتورة — لا توجد أصناف."); return; }
            try { _receipt.ShowSale(Session.Services.Pos.GetSale(saleId.Value)); }
            catch (Exception ex) { _receipt.ShowMessage("تعذّر تحميل الفاتورة: " + ex.Message); }
        }

        private void RecordPayment()
        {
            string s = Prompt.Show("مبلغ الدفعة", "تسجيل دفعة");
            if (string.IsNullOrWhiteSpace(s)) return;
            if (!MoneyInput.TryParse(s, out decimal amount)) { Msg.Warn("مبلغ غير صالح."); return; }
            try { Session.Services.Debts.RecordPayment(Session.CurrentUser, _customerId, amount); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void AddCharge()
        {
            string s = Prompt.Show("مبلغ الدين", "إضافة دين");
            if (string.IsNullOrWhiteSpace(s)) return;
            if (!MoneyInput.TryParse(s, out decimal amount)) { Msg.Warn("مبلغ غير صالح."); return; }
            string note = Prompt.Show("ملاحظة (اختياري)", "إضافة دين");
            try { Session.Services.Debts.RecordManualCharge(Session.CurrentUser, _customerId, amount, note); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private static Button Btn(string text, Action onClick, bool primary = false)
            => Theme.ActionButton(text, onClick, primary, width: 140);
    }
}
