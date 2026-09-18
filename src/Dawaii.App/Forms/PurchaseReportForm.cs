using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// The manager's view of buying (V2.2): every order placed in a period — who placed it, when, from
    /// which company, and what is still owed on it — with the outstanding total per company underneath.
    ///
    /// Two questions, one screen, because they are asked together. The top half answers "who committed
    /// this money and when", which is why the employee column exists at all: filing an order is the one
    /// thing a "موظف ذو امتيازات" does that spends the pharmacy's money, and the manager needs it
    /// attributable. The bottom half answers "what do we owe, and to whom" — which is not a property of
    /// the period at all, so it deliberately ignores the dates and sums every unpaid invoice ever filed.
    /// Reporting a debt only because it happened to fall inside the chosen month would understate what
    /// the pharmacy actually owes.
    /// </summary>
    public class PurchaseReportForm : BaseForm
    {
        private DateTimePicker _from, _to;
        private DataGridView _orders, _companies;
        private Label _summary;
        private List<PurchaseInvoice> _rows = new List<PurchaseInvoice>();

        public PurchaseReportForm()
        {
            Text = "تقرير المشتريات والموردين";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(1000, 740);

            var title = new Label
            {
                Text = "تقرير المشتريات والموردين", Dock = DockStyle.Top, Height = 44,
                Font = Theme.Title(18f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10, 8, 10, 8) };
            bar.Controls.Add(new Label { Text = "من", AutoSize = true, Margin = new Padding(6, 8, 4, 0), Font = Theme.Base(11.5f) });
            _from = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 130, Font = Theme.Base(11.5f), Value = DateTime.Today.AddMonths(-1) };
            bar.Controls.Add(_from);
            bar.Controls.Add(new Label { Text = "إلى", AutoSize = true, Margin = new Padding(10, 8, 4, 0), Font = Theme.Base(11.5f) });
            _to = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 130, Font = Theme.Base(11.5f), Value = DateTime.Today };
            bar.Controls.Add(_to);
            bar.Controls.Add(Theme.ActionButton("عرض", Reload, primary: true, width: 110));
            bar.Controls.Add(Theme.ActionButton("طباعة A4", PrintSelected, width: 130));
            // Reviewing the orders is where a wrong one is spotted, so it can be corrected from here
            // too rather than hunting the company down in الموردون first (V2.3).
            bar.Controls.Add(Theme.ActionButton("تعديل الفاتورة", EditSelected, width: 140));

            _summary = new Label
            {
                Dock = DockStyle.Top, Height = 52, Font = Theme.Base(12.5f, FontStyle.Bold), ForeColor = Theme.Primary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 6, 16, 6)
            };

            _orders = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_orders);
            Col(_orders, "التاريخ", "Date", 110);
            Col(_orders, "رقم الفاتورة", "Number", 130);
            Col(_orders, "الشركة", "Company", 0, fill: true);
            Col(_orders, "المندوب", "Rep", 140);
            Col(_orders, "أدخلها", "User", 140);
            Col(_orders, "وقت الإدخال", "Entered", 150);
            Col(_orders, "الأصناف", "Count", 80);
            Col(_orders, "الإجمالي", "Total", 120);
            Col(_orders, "المتبقي", "Outstanding", 120);
            _orders.CellDoubleClick += (s, e) => PrintSelected();
            _orders.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _rows[e.RowIndex].Outstanding > 0m)
                    e.CellStyle.ForeColor = Theme.Danger;
            };

            var companiesCaption = new Label
            {
                Text = "المستحق لكل شركة (كل الفواتير غير المسددة، بغض النظر عن الفترة)",
                Dock = DockStyle.Top, Height = 28, Font = Theme.Base(12f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };
            _companies = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_companies);
            Col(_companies, "الشركة", "Name", 0, fill: true);
            Col(_companies, "عدد الفواتير", "Invoices", 130);
            Col(_companies, "المستحق", "Outstanding", 170);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 240, BackColor = Theme.Background };
            bottom.Controls.Add(_companies);
            bottom.Controls.Add(companiesCaption);

            var close = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            close.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 130));

            Controls.Add(_orders);
            Controls.Add(bottom);
            Controls.Add(close);
            Controls.Add(_summary);
            Controls.Add(bar);
            Controls.Add(title);

            Reload();
        }

        private void Reload()
        {
            try
            {
                // The picker gives a day; the query wants a half-open range, so "to" is pushed to the
                // start of the next day — otherwise an order placed today is missing from a report that
                // says it runs up to today.
                DateTime from = _from.Value.Date;
                DateTime to = _to.Value.Date.AddDays(1);
                if (to <= from) { Msg.Warn("الفترة غير صالحة."); return; }

                _rows = Session.Services.Suppliers.OrdersInRange(Session.CurrentUser, from, to).ToList();

                _orders.DataSource = _rows.Select(i => new
                {
                    Date = Fmt.Date(i.InvoiceDate),
                    Number = string.IsNullOrWhiteSpace(i.InvoiceNumber) ? "—" : i.InvoiceNumber,
                    Company = i.SupplierName,
                    Rep = i.Representative ?? "—",
                    User = i.UserName ?? "—",
                    Entered = Fmt.DateTime(i.CreatedAt),
                    Count = i.LineCount,
                    Total = Fmt.Money(i.Total),
                    Outstanding = Fmt.Money(i.Outstanding)
                }).ToList();

                var owed = Session.Services.Suppliers.WithOutstanding();
                _companies.DataSource = owed.Select(s => new
                {
                    s.Name,
                    Invoices = s.InvoiceCount,
                    Outstanding = Fmt.Money(s.Outstanding)
                }).ToList();

                _summary.Text =
                    "عدد الطلبات في الفترة: " + _rows.Count +
                    "     قيمة المشتريات: " + Fmt.Money(_rows.Sum(i => i.Total)) +
                    "     إجمالي المستحق للموردين: " + Fmt.Money(Session.Services.Suppliers.TotalOutstanding());
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        /// <summary>Prints the selected order. Re-read in full first: the report's rows carry no lines,
        /// so printing one straight from the grid would produce a sheet with no medicines on it.</summary>
        private void PrintSelected()
        {
            int idx = _orders.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _rows.Count) { Msg.Info("اختر طلباً."); return; }

            try
            {
                PurchaseInvoice full = Session.Services.Suppliers.GetInvoice(_rows[idx].Id);
                if (full == null) { Msg.Warn("تعذّر تحميل الفاتورة."); return; }

                PurchaseInvoicePrinter.Preview(this, full, Session.Services.CreateReceiptInfo(null).PharmacyName);
            }
            catch (Exception ex) { Msg.Error("تعذّرت الطباعة: " + ex.Message); }
        }

        /// <summary>Opens the selected order for correction, then re-runs the report so the corrected
        /// figures — and any change to what is owed — are the ones on screen.</summary>
        private void EditSelected()
        {
            int idx = _orders.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _rows.Count) { Msg.Info("اختر طلباً."); return; }

            try
            {
                using (var f = new PurchaseInvoiceForm(_rows[idx].Id)) f.ShowDialog(this);
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر فتح الفاتورة: " + ex.Message); }
            Reload();
        }

        private static void Col(DataGridView g, string header, string prop, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            g.Columns.Add(c);
        }
    }
}
