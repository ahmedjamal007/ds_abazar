using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// What was actually handed over for an invoice (V2.1): all of it, some of it, or none.
    ///
    /// A separate screen from the invoice itself, because the two are separate facts. What arrived is
    /// settled the moment the boxes are counted; what was paid may be settled at the door, at the end
    /// of the week, or in instalments. So this screen is shown once straight after filing a delivery,
    /// and can be reopened from the supplier's invoice list every time another payment is made.
    ///
    /// The three choices are a shortcut, not a stored status: each one records a payment of a
    /// particular size, and the invoice works out where it stands from the amounts. Nothing here can
    /// leave an invoice claiming to be paid while it still owes money.
    /// </summary>
    public class InvoicePaymentForm : BaseForm
    {
        private readonly int _invoiceId;
        private PurchaseInvoice _invoice;

        private Label _summary;
        private RadioButton _full, _partial, _none;
        private NumericUpDown _amount;
        private ComboBox _method;
        private DataGridView _history;

        public InvoicePaymentForm(int invoiceId)
        {
            _invoiceId = invoiceId;

            Text = "سداد فاتورة المورد";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(560, 560);

            _summary = new Label
            {
                Dock = DockStyle.Top, Height = 96, Font = Theme.Base(12.5f), ForeColor = Theme.TextPrimary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 8, 16, 8)
            };

            var choices = new GroupBox
            {
                Text = "حالة السداد", Dock = DockStyle.Top, Height = 190, Font = Theme.Base(12f),
                ForeColor = Theme.TextPrimary, Padding = new Padding(14, 10, 14, 10)
            };
            _full = new RadioButton { Text = "مدفوعة بالكامل", Dock = DockStyle.Top, Height = 32, Font = Theme.Base(12f), Checked = true };
            _partial = new RadioButton { Text = "مدفوعة جزئياً", Dock = DockStyle.Top, Height = 32, Font = Theme.Base(12f) };
            _none = new RadioButton { Text = "غير مدفوعة", Dock = DockStyle.Top, Height = 32, Font = Theme.Base(12f) };

            _amount = new NumericUpDown
            {
                Dock = DockStyle.Top, Minimum = 0, Maximum = 100000000, DecimalPlaces = 2,
                Font = Theme.Base(12.5f), TextAlign = HorizontalAlignment.Center, Enabled = false
            };

            // How the company was paid. Only cash leaves the till, and the shift report subtracts
            // exactly that from the expected drawer — so a bank transfer must be recordable as one,
            // or fixing the missing subtraction would just create the opposite error.
            _method = new ComboBox
            {
                Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                Font = Theme.Base(12f), Height = 30
            };
            _method.Items.AddRange(new object[] { "نقداً من الدرج", "تحويل بنكي" });
            _method.SelectedIndex = 0;

            // Added bottom-up: each is Dock.Top, so the last added sits highest.
            choices.Controls.Add(_method);
            choices.Controls.Add(_amount);
            choices.Controls.Add(_none);
            choices.Controls.Add(_partial);
            choices.Controls.Add(_full);

            _partial.CheckedChanged += (s, e) => _amount.Enabled = _partial.Checked;

            var historyLabel = new Label
            {
                Text = "الدفعات المسجلة", Dock = DockStyle.Top, Height = 28, Font = Theme.Base(12f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };
            _history = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_history);
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "التاريخ", DataPropertyName = "Date", Width = 160 });
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المبلغ", DataPropertyName = "Amount", Width = 130 });
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ملاحظة", DataPropertyName = "Note", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton("تسجيل", Apply, primary: true, width: 150));
            bar.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 130));

            Controls.Add(_history);
            Controls.Add(historyLabel);
            Controls.Add(choices);
            Controls.Add(_summary);
            Controls.Add(bar);

            Reload();
        }

        private void Reload()
        {
            try
            {
                _invoice = Session.Services.Suppliers.GetInvoice(_invoiceId);
                if (_invoice == null) { Msg.Warn("الفاتورة غير موجودة."); Close(); return; }

                _summary.Text =
                    "الشركة: " + _invoice.SupplierName + "\n" +
                    "فاتورة رقم: " + (string.IsNullOrWhiteSpace(_invoice.InvoiceNumber) ? "—" : _invoice.InvoiceNumber) +
                    "     التاريخ: " + Fmt.Date(_invoice.InvoiceDate) + "\n" +
                    "الإجمالي: " + Fmt.Money(_invoice.Total) +
                    "     المدفوع: " + Fmt.Money(_invoice.AmountPaid) +
                    "     المتبقي: " + Fmt.Money(_invoice.Outstanding);

                _history.DataSource = Session.Services.Suppliers.GetPayments(_invoiceId)
                    .Select(p => new { Date = Fmt.DateTime(p.CreatedAt), Amount = Fmt.Money(p.Amount), Note = p.Note ?? "" })
                    .ToList();

                // A settled invoice has nothing left to record, so the whole choice block goes quiet
                // rather than offering buttons that would only be refused.
                bool settled = _invoice.Outstanding <= 0m;
                _full.Enabled = _partial.Enabled = _none.Enabled = !settled;
                _amount.Enabled = !settled && _partial.Checked;
                _amount.Maximum = Math.Max(0m, _invoice.Outstanding);
                if (_amount.Value > _amount.Maximum) _amount.Value = _amount.Maximum;
                if (settled) _summary.Text += "\nالفاتورة مسددة بالكامل.";
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void Apply()
        {
            if (_invoice == null || _invoice.Outstanding <= 0m) { Close(); return; }

            try
            {
                if (_none.Checked)
                {
                    // Nothing changes: an unpaid invoice already owes its whole total. Saying so
                    // explicitly is how the user leaves this screen having answered the question.
                    Close();
                    return;
                }

                string method = _method.SelectedIndex == 1 ? "Bank" : "Cash";
                string methodLabel = _method.SelectedIndex == 1 ? "تحويل بنكي" : "نقداً من الدرج";
                decimal paying = _full.Checked ? _invoice.Outstanding : decimal.Round(_amount.Value, 2);
                if (paying <= 0m) { Msg.Warn("أدخل مبلغ الدفعة."); return; }

                // Money leaving the pharmacy gets the same confirmation a deletion does. It did not
                // before, which made the most consequential button on the screen the least protected.
                if (!Msg.Confirm(
                        "تسجيل دفعة " + Fmt.Money(paying) + " (" + methodLabel + ") " +
                        "للشركة " + (_invoice.SupplierName ?? "") + "؟"))
                    return;

                if (_full.Checked)
                {
                    Session.Services.Suppliers.SettleInvoice(Session.CurrentUser, _invoiceId, method);
                }
                else
                {
                    Session.Services.Suppliers.RecordPayment(
                        Session.CurrentUser, _invoiceId, paying, "دفعة جزئية", method);
                }

                Reload();
                Msg.Info("تم تسجيل السداد.");
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }
    }
}
