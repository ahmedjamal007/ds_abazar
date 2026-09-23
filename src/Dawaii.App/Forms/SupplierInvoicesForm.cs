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
    /// One company's account (V2.1): every invoice filed against them, what each still owes, and what
    /// they are owed in total.
    ///
    /// Laid out like the customer's كشف حساب on purpose — a list of documents with the detail of the
    /// selected one underneath — because it answers the same question from the other side of the
    /// counter: "what is this figure for?" There the rows are sales and the detail is what the customer
    /// took; here they are deliveries and the detail is what the company brought.
    ///
    /// The search matches the three things written on the paper — invoice number, representative and
    /// company name — because those are what someone holding an invoice actually has to go on. It
    /// normally stays inside this company's file, but "كل الشركات" widens it to every supplier, which
    /// is the case the number alone has to answer: a delivery note turns up and nobody remembers which
    /// company sent it.
    /// </summary>
    public class SupplierInvoicesForm : BaseForm
    {
        private readonly int _supplierId;
        private Supplier _supplier;

        private Label _header;
        private TextBox _search;
        private CheckBox _allCompanies;
        private DataGridViewTextBoxColumn _companyColumn;
        private DataGridView _invoices;
        private DataGridView _lines;
        private Label _linesCaption;
        private List<PurchaseInvoice> _rows = new List<PurchaseInvoice>();

        public SupplierInvoicesForm(int supplierId)
        {
            _supplierId = supplierId;

            Text = "حساب الشركة";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(1040, 720);

            _header = new Label
            {
                Dock = DockStyle.Top, Height = 60, Font = Theme.Base(13.5f, FontStyle.Bold), ForeColor = Theme.Primary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 6, 16, 6)
            };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _search.TextChanged += (s, e) => Reload();

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10, 6, 10, 6) };
            toolbar.Controls.Add(Theme.ActionButton("فاتورة جديدة", NewInvoice, primary: true, width: 150));
            // Correcting is offered right beside filing (V2.3): a wrong number or quantity is noticed
            // while looking at the company's file, which is this screen.
            toolbar.Controls.Add(Theme.ActionButton("تعديل الفاتورة", EditSelected, width: 140));
            toolbar.Controls.Add(Theme.ActionButton("سداد / دفعة", OpenPayment, width: 150));
            toolbar.Controls.Add(Theme.ActionButton("تعديل الشركة", RenameCompany, width: 130));
            // Printing the sheet that goes in the folder is the manager's (V2.2).
            if (Session.IsAdmin) toolbar.Controls.Add(Theme.ActionButton("طباعة A4", PrintSelected, width: 130));
            toolbar.Controls.Add(Theme.ActionButton("تحديث", Reload, width: 110));

            _allCompanies = new CheckBox { Text = "بحث في كل الشركات", AutoSize = true, Margin = new Padding(12, 16, 6, 0), Font = Theme.Base(11f) };
            _allCompanies.CheckedChanged += (s, e) => Reload();
            toolbar.Controls.Add(_allCompanies);

            _invoices = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_invoices);
            // Redundant while looking at one company's file, so it appears only once the search widens.
            _companyColumn = new DataGridViewTextBoxColumn { HeaderText = "الشركة", DataPropertyName = "Company", Width = 170, Visible = false };
            _invoices.Columns.Add(_companyColumn);
            InvoiceCol("رقم الفاتورة", "Number", 140);
            InvoiceCol("التاريخ", "Date", 120);
            InvoiceCol("المندوب", "Rep", 0, fill: true);
            InvoiceCol("الأصناف", "Count", 80);
            InvoiceCol("الإجمالي", "Total", 130);
            InvoiceCol("المدفوع", "Paid", 130);
            InvoiceCol("المتبقي", "Outstanding", 130);
            InvoiceCol("الحالة", "Status", 120);
            _invoices.SelectionChanged += (s, e) => ShowSelectedLines();
            _invoices.CellDoubleClick += (s, e) => OpenPayment();
            // An invoice still owing money is worth spotting from across the room.
            _invoices.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _rows[e.RowIndex].Outstanding > 0m)
                    e.CellStyle.ForeColor = Theme.Danger;
            };

            _linesCaption = new Label
            {
                Dock = DockStyle.Top, Height = 28, Font = Theme.Base(12f, FontStyle.Bold), ForeColor = Theme.TextPrimary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };
            _lines = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_lines);
            LineCol("الصنف", "Name", 0, fill: true);
            LineCol("العلب", "Boxes", 80);
            LineCol("أشرطة/علبة", "Strips", 100);
            LineCol("شراء العلبة", "Buy", 120);
            LineCol("بيع العلبة", "Sell", 120);
            LineCol("الصلاحية", "Expiry", 110);
            LineCol("القيمة", "Total", 130);

            var detail = new Panel { Dock = DockStyle.Bottom, Height = 260, BackColor = Theme.Background };
            detail.Controls.Add(_lines);
            detail.Controls.Add(_linesCaption);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 130));

            Controls.Add(_invoices);
            Controls.Add(detail);
            Controls.Add(bar);
            Controls.Add(toolbar);
            Controls.Add(_search);
            Controls.Add(_header);

            Reload();
        }

        private void Reload()
        {
            try
            {
                _supplier = Session.Services.Suppliers.Get(_supplierId);
                if (_supplier == null) { Msg.Warn("الشركة غير موجودة."); Close(); return; }

                string term = _search.Text.Trim();
                bool everyCompany = _allCompanies.Checked;

                // With no term and no widening this is the company's whole file, which is what the screen
                // opens on; the search only ever narrows what is already shown.
                _rows = Session.Services.Suppliers
                    .SearchInvoices(everyCompany ? (int?)null : _supplierId, term).ToList();

                _companyColumn.Visible = everyCompany;

                _header.Text =
                    _supplier.Name + "\n" +
                    "إجمالي المستحق عليها: " + Fmt.Money(_supplier.Outstanding) +
                    "     " + ListingCaption(term, everyCompany, _rows.Count);

                _invoices.DataSource = _rows.Select(i => new
                {
                    Company = i.SupplierName,
                    Number = string.IsNullOrWhiteSpace(i.InvoiceNumber) ? "—" : i.InvoiceNumber,
                    Date = Fmt.Date(i.InvoiceDate),
                    Rep = i.Representative ?? "—",
                    Count = i.LineCount,
                    Total = Fmt.Money(i.Total),
                    Paid = Fmt.Money(i.AmountPaid),
                    Outstanding = Fmt.Money(i.Outstanding),
                    Status = StatusText(i.Status)
                }).ToList();

                ShowSelectedLines();
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        /// <summary>Says what the grid is currently showing, so a short list is never mistaken for the
        /// company having few invoices when it is really the search having narrowed them.</summary>
        private static string ListingCaption(string term, bool everyCompany, int count)
        {
            if (term.Length == 0 && !everyCompany) return "عدد الفواتير: " + count;
            string scope = everyCompany ? "في كل الشركات" : "في هذه الشركة";
            return term.Length == 0
                ? "الفواتير " + scope + ": " + count
                : "نتائج البحث " + scope + ": " + count;
        }

        /// <summary>The medicines on the selected invoice. The listing query does not carry lines — a
        /// company with years of deliveries would be loading thousands of rows to show one — so the
        /// selected invoice is re-read in full.</summary>
        private void ShowSelectedLines()
        {
            PurchaseInvoice selected = Selected();
            if (selected == null)
            {
                _linesCaption.Text = "اختر فاتورة لعرض أصنافها.";
                _lines.DataSource = null;
                return;
            }

            try
            {
                PurchaseInvoice full = Session.Services.Suppliers.GetInvoice(selected.Id);
                if (full == null) { _linesCaption.Text = "تعذّر تحميل الفاتورة."; _lines.DataSource = null; return; }

                _linesCaption.Text =
                    "أصناف الفاتورة " + (string.IsNullOrWhiteSpace(full.InvoiceNumber) ? "#" + full.Id : full.InvoiceNumber) +
                    "  —  " + Fmt.Date(full.InvoiceDate) +
                    "  —  الإجمالي " + Fmt.Money(full.Total) +
                    "  —  المتبقي " + Fmt.Money(full.Outstanding);

                _lines.DataSource = full.Lines.Select(l => new
                {
                    Name = l.ItemName,
                    Boxes = l.QuantityBoxes,
                    Strips = l.StripsPerBox,
                    Buy = Fmt.Money(l.BoxPurchasePrice),
                    Sell = Fmt.Money(l.BoxSellingPrice),
                    Expiry = l.ExpiryDate.HasValue ? Fmt.Date(l.ExpiryDate) : "—",
                    Total = Fmt.Money(l.LineTotal)
                }).ToList();
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private PurchaseInvoice Selected()
        {
            int idx = _invoices.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
        }

        private void NewInvoice()
        {
            // The delivery window is modeless so it can be minimized mid-typing. That only helps if
            // this listing steps aside: it is modal, and would go on blocking the app behind it.
            bool refused = PurchaseInvoiceForm.IsOpen;   // it will surface the open one instead
            PurchaseInvoiceForm.OpenAlongside(_supplier, this);
            if (!refused) Close();
        }

        /// <summary>
        /// Corrects the selected invoice (V2.3) — company, representative, number, date and medicines.
        ///
        /// The row in the grid carries no lines, so the editor re-reads the invoice in full; opening it
        /// on the listed row would present a delivery with nothing on it and save it that way.
        /// </summary>
        private void EditSelected()
        {
            PurchaseInvoice selected = Selected();
            if (selected == null) { Msg.Info("اختر فاتورة."); return; }

            try
            {
                using (var f = new PurchaseInvoiceForm(selected.Id)) f.ShowDialog(this);
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر فتح الفاتورة: " + ex.Message); }
            Reload();
        }

        /// <summary>Fixes a misspelled company name from the screen where it is being read, rather than
        /// sending the user back to the companies list to do it.</summary>
        private void RenameCompany()
        {
            if (_supplier == null) return;

            string name = Prompt.Show("اسم الشركة", "تعديل اسم الشركة", _supplier.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == _supplier.Name) return;

            try { Session.Services.Suppliers.RenameSupplier(Session.CurrentUser, _supplier.Id, name); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            Reload();
        }

        /// <summary>Prints the selected order on A4, through a preview so the manager sees the sheet
        /// before it reaches paper. The listing rows carry no lines, so the invoice is re-read in full —
        /// printing the listed row would produce a sheet with no medicines on it.</summary>
        private void PrintSelected()
        {
            PurchaseInvoice selected = Selected();
            if (selected == null) { Msg.Info("اختر فاتورة."); return; }

            try
            {
                PurchaseInvoice full = Session.Services.Suppliers.GetInvoice(selected.Id);
                if (full == null) { Msg.Warn("تعذّر تحميل الفاتورة."); return; }

                PurchaseInvoicePrinter.Preview(this, full, Session.Services.CreateReceiptInfo(null).PharmacyName);
            }
            catch (Exception ex) { Msg.Error("تعذّرت الطباعة: " + ex.Message); }
        }

        private void OpenPayment()
        {
            PurchaseInvoice selected = Selected();
            if (selected == null) { Msg.Info("اختر فاتورة."); return; }

            using (var f = new InvoicePaymentForm(selected.Id)) f.ShowDialog(this);
            Reload();
        }

        internal static string StatusText(PurchaseInvoiceStatus status)
        {
            switch (status)
            {
                case PurchaseInvoiceStatus.Paid: return "مدفوعة";
                case PurchaseInvoiceStatus.PartiallyPaid: return "مدفوعة جزئياً";
                default: return "غير مدفوعة";
            }
        }

        private void InvoiceCol(string header, string prop, int width, bool fill = false)
            => AddCol(_invoices, header, prop, width, fill);

        private void LineCol(string header, string prop, int width, bool fill = false)
            => AddCol(_lines, header, prop, width, fill);

        private static void AddCol(DataGridView g, string header, string prop, int width, bool fill)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            g.Columns.Add(c);
        }
    }
}
