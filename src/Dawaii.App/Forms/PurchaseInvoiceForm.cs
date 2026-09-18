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
    /// A delivery from one supplier (V2.1): the paperwork at the top, the medicines underneath.
    ///
    /// Adding a drug goes through the same picker the stock screens use, so a medicine already in the
    /// catalog is found and a new one is created on the spot — either way the invoice ends up holding
    /// a real item id, and the item's own details are left exactly as they were. Saving turns every
    /// line into a stock batch, so what the invoice says arrived and what the shelf holds are written
    /// together or not at all.
    ///
    /// The same screen also CORRECTS an invoice already on file (V2.3). Until then a delivery could be
    /// typed once and never touched, so a mistyped quantity or price stayed wrong forever and the only
    /// way out was a second invoice cancelling the first. Correcting is deliberately the same screen as
    /// filing: it is the same document, and someone fixing a number should not have to learn a second
    /// layout to do it. What differs is that the company can be changed — an invoice filed against the
    /// wrong company is one of the mistakes worth being able to undo — and that saving does not go on
    /// to the payment screen, because money already handed over is not part of the correction.
    ///
    /// The total is added up from the lines and cannot be typed: it is what the pharmacy will owe, and
    /// a figure disagreeing with the boxes actually received would be a payable nobody could reconcile.
    /// </summary>
    public class PurchaseInvoiceForm : BaseForm
    {
        private Supplier _supplier;

        /// <summary>The invoice being corrected, or null when a new one is being filed.</summary>
        private readonly PurchaseInvoice _editing;

        private readonly List<PurchaseInvoiceLine> _lines = new List<PurchaseInvoiceLine>();

        private TextBox _representative, _invoiceNumber;
        private DateTimePicker _date;
        private ComboBox _company;
        private DataGridView _grid;
        private Label _total;

        /// <summary>The invoice that was filed or corrected, or null if the user backed out.</summary>
        public PurchaseInvoice Saved { get; private set; }

        /// <summary>Files a new delivery from this company.</summary>
        public PurchaseInvoiceForm(Supplier supplier) : this(supplier, null) { }

        /// <summary>Corrects an invoice already on file. It is re-read here rather than taken from the
        /// listing that opened it — a listing row carries no lines, and correcting an invoice whose
        /// lines were never loaded would file it again as an empty delivery.</summary>
        public PurchaseInvoiceForm(int invoiceId) : this(null, LoadForEdit(invoiceId)) { }

        private PurchaseInvoiceForm(Supplier supplier, PurchaseInvoice editing)
        {
            _editing = editing;
            _supplier = supplier;
            bool correcting = editing != null;

            if (correcting)
            {
                _supplier = Session.Services.Suppliers.Get(editing.SupplierId);
                _lines.AddRange(editing.Lines);
            }
            else if (_supplier == null)
            {
                throw new ArgumentNullException(nameof(supplier));
            }

            Text = correcting ? "تعديل فاتورة مشتريات" : "فاتورة مشتريات جديدة";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(900, 700);

            var title = new Label
            {
                Text = correcting
                    ? "تعديل الفاتورة " + (string.IsNullOrWhiteSpace(editing.InvoiceNumber)
                        ? "#" + editing.Id : editing.InvoiceNumber)
                    : "فاتورة من: " + _supplier.Name,
                Dock = DockStyle.Top, Height = 44, Font = Theme.Title(16f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true,
                Padding = new Padding(14, 6, 14, 6), RightToLeft = RightToLeft.Yes
            };
            for (int i = 0; i < 4; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            _representative = HeaderText(header, "المندوب / الرقم", 0);
            _invoiceNumber = HeaderText(header, "رقم الفاتورة", 2);
            _date = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Short, Font = Theme.Base(11.5f), Value = DateTime.Today };
            header.Controls.Add(Caption("تاريخ الفاتورة"), 0, 2);
            header.Controls.Add(_date, 1, 2);

            // Only when correcting: the company is part of what may have been typed wrongly. On a new
            // invoice it is settled by the row the user opened this from, and offering it again would
            // just be one more thing to get wrong.
            if (correcting)
            {
                _company = new ComboBox
                {
                    Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = Theme.Base(11.5f), DisplayMember = "Name", ValueMember = "Id"
                };
                List<Supplier> companies = Session.Services.Suppliers.Search("").ToList();
                // The listing is capped, so a pharmacy with a long book of companies could open an
                // invoice whose own company is not in it — which would silently re-file the invoice
                // against whichever company happened to be first.
                if (_supplier != null && companies.All(c => c.Id != editing.SupplierId))
                    companies.Insert(0, _supplier);
                _company.DataSource = companies;
                _company.SelectedIndex = companies.FindIndex(c => c.Id == editing.SupplierId);
                header.Controls.Add(Caption("الشركة"), 2, 2);
                header.Controls.Add(_company, 3, 2);
            }

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10, 6, 10, 6) };
            toolbar.Controls.Add(Theme.ActionButton("إضافة صنف", AddLine, primary: true, width: 140));
            toolbar.Controls.Add(Theme.ActionButton("تعديل السطر", EditLine, width: 130));
            toolbar.Controls.Add(Theme.ActionButton("حذف السطر", RemoveLine, width: 130));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_grid);
            Col("الصنف", "Name", fill: true);
            Col("العلب", "Boxes", 80);
            Col("أشرطة/علبة", "Strips", 100);
            Col("شراء العلبة", "Buy", 120);
            Col("بيع العلبة", "Sell", 120);
            Col("الصلاحية", "Expiry", 110);
            Col("القيمة", "Total", 130);
            _grid.CellDoubleClick += (s, e) => EditLine();

            _total = new Label
            {
                Dock = DockStyle.Bottom, Height = 44, Font = Theme.Base(14f, FontStyle.Bold), ForeColor = Theme.Primary,
                BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(16, 6, 16, 6)
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton(
                correcting ? "حفظ التعديلات" : "حفظ ومتابعة للدفع", Save, primary: true, width: 200));
            bar.Controls.Add(Theme.ActionButton("إلغاء", Close, width: 130));

            Controls.Add(_grid);
            Controls.Add(_total);
            Controls.Add(bar);
            Controls.Add(toolbar);
            Controls.Add(header);
            Controls.Add(title);

            if (correcting)
            {
                _representative.Text = editing.Representative ?? "";
                _invoiceNumber.Text = editing.InvoiceNumber ?? "";
                _date.Value = editing.InvoiceDate.Date;
            }

            Reload();
        }

        /// <summary>Reads the invoice in full — lines included — for correction.</summary>
        private static PurchaseInvoice LoadForEdit(int invoiceId)
        {
            PurchaseInvoice invoice = Session.Services.Suppliers.GetInvoice(invoiceId);
            if (invoice == null) throw new ValidationException("الفاتورة غير موجودة.");
            return invoice;
        }

        // ---------------- lines ----------------

        /// <summary>
        /// Picks the medicine, then asks what arrived. A drug already in the catalog is used as it
        /// stands — the delivery adds a batch, it does not rewrite the item — and one the pharmacy has
        /// never stocked is created through the same "صنف جديد" flow the stock screens use.
        /// </summary>
        private void AddLine()
        {
            Item item;
            using (var picker = new ItemPickerForm())
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
                item = picker.Selected;
            }

            using (var line = new PurchaseLineForm(item))
            {
                if (line.ShowDialog(this) != DialogResult.OK || line.Result == null) return;
                _lines.Add(line.Result);
            }
            Reload();
        }

        private void EditLine()
        {
            int idx = SelectedIndex();
            if (idx < 0) { Msg.Info("اختر سطراً."); return; }

            PurchaseInvoiceLine existing = _lines[idx];
            Item item = Session.Services.Items.GetById(existing.ItemId);
            if (item == null) { Msg.Warn("الصنف لم يعد موجوداً."); return; }

            using (var line = new PurchaseLineForm(item, existing))
            {
                if (line.ShowDialog(this) != DialogResult.OK || line.Result == null) return;
                _lines[idx] = line.Result;
            }
            Reload();
        }

        /// <summary>
        /// Takes a medicine off the invoice. On a new invoice that is just a row disappearing; on one
        /// already filed it also takes the batch off the shelf when saved, which the service refuses if
        /// anything has been sold from it — so the confirmation says which kind of removal this is.
        /// </summary>
        private void RemoveLine()
        {
            int idx = SelectedIndex();
            if (idx < 0) { Msg.Info("اختر سطراً."); return; }

            if (_editing != null && _lines[idx].Id > 0 &&
                !Msg.Confirm("حذف \"" + _lines[idx].ItemName + "\" من الفاتورة سيحذف دفعته من المخزون أيضاً. متابعة؟"))
                return;

            _lines.RemoveAt(idx);
            Reload();
        }

        private int SelectedIndex()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _lines.Count ? idx : -1;
        }

        private void Reload()
        {
            _grid.DataSource = _lines.Select(l => new
            {
                Name = l.ItemName,
                Boxes = l.QuantityBoxes,
                Strips = l.StripsPerBox,
                Buy = Fmt.Money(l.BoxPurchasePrice),
                Sell = Fmt.Money(l.BoxSellingPrice),
                Expiry = l.ExpiryDate.HasValue ? Fmt.Date(l.ExpiryDate) : "—",
                Total = Fmt.Money(l.LineTotal)
            }).ToList();

            decimal total = _lines.Sum(l => l.LineTotal);
            _total.Text = "إجمالي الفاتورة: " + Fmt.Money(total) + "   —   " + _lines.Count + " صنف";

            // Correcting an invoice that has been paid on has a floor: the total cannot drop below what
            // the company has already been handed. Showing it here beats finding out at save time.
            if (_editing != null && _editing.AmountPaid > 0m)
                _total.Text += "   —   المدفوع: " + Fmt.Money(_editing.AmountPaid) +
                               (total < _editing.AmountPaid ? "  ⚠ الإجمالي أقل من المدفوع" : "");
        }

        // ---------------- save ----------------

        /// <summary>
        /// Files the invoice, then goes straight on to the payment screen. The two are one errand — the
        /// person who just typed a delivery in knows whether they paid for it — so the invoice is saved
        /// unpaid and the payment recorded against it a moment later, which is also what happens when a
        /// company is paid weeks after delivering.
        ///
        /// Correcting an existing invoice stops after the save: the payments on it are already recorded,
        /// and re-opening the payment screen would invite paying the same delivery twice.
        /// </summary>
        private void Save()
        {
            if (_lines.Count == 0) { Msg.Warn("أضف صنفاً واحداً على الأقل."); return; }

            try
            {
                if (_editing == null)
                {
                    PurchaseInvoice invoice = Session.Services.Suppliers.RecordInvoice(
                        Session.CurrentUser, _supplier.Id, _representative.Text, _invoiceNumber.Text,
                        _date.Value.Date, _lines);

                    Saved = invoice;

                    using (var pay = new InvoicePaymentForm(invoice.Id))
                        pay.ShowDialog(this);
                }
                else
                {
                    int supplierId = _company != null && _company.SelectedItem is Supplier chosen
                        ? chosen.Id
                        : _editing.SupplierId;

                    Saved = Session.Services.Suppliers.EditInvoice(
                        Session.CurrentUser, _editing.Id, supplierId, _representative.Text,
                        _invoiceNumber.Text, _date.Value.Date, _lines);

                    Msg.Info("تم حفظ التعديلات.");
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر حفظ الفاتورة: " + ex.Message); }
        }

        // ---------------- layout helpers ----------------

        private void Col(string header, string prop, int width = 0, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            _grid.Columns.Add(c);
        }

        private static Label Caption(string text) => new Label
        {
            Text = text, Dock = DockStyle.Fill, Font = Theme.Base(11.5f),
            TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(4, 8, 4, 4)
        };

        private static TextBox HeaderText(TableLayoutPanel t, string label, int column)
        {
            var b = new TextBox { Dock = DockStyle.Fill, Font = Theme.Base(11.5f), Margin = new Padding(4, 6, 4, 4) };
            t.Controls.Add(Caption(label), column, 0);
            t.Controls.Add(b, column + 1, 0);
            return b;
        }
    }
}
