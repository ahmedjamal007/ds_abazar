using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

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

        /// <summary>
        /// The draft this screen is working inside, or 0 when it has never been set aside. A NEW
        /// delivery keeps its typing when the screen closes; an edit of an invoice already on file
        /// does not — see <see cref="PurchaseDrafts"/>.
        /// </summary>
        private int _draftNumber;

        /// <summary>True once the invoice is saved, so closing afterwards does not file a draft of
        /// something that is already on the books.</summary>
        private bool _committed;

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

        /// <summary>Reopens a delivery that was set aside, exactly as it was left.</summary>
        public static PurchaseInvoiceForm Resume(PurchaseDrafts.Draft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            var form = new PurchaseInvoiceForm(draft.Supplier, null);
            form.LoadDraft(draft);
            return form;
        }

        // ---------------- one delivery window, open beside the app ----------------

        /// <summary>
        /// The delivery window currently open, or null.
        ///
        /// A delivery being typed is shown MODELESSLY (V2.3.2) so it can be minimized while the
        /// customer who just walked in is served. A minimize button on its own would not have helped:
        /// a modal window keeps the main window disabled underneath it, so the pharmacist would have
        /// been left staring at a program that ignores every click.
        ///
        /// Only one may be open. Two would be filing into the same draft, and whichever was saved
        /// second would quietly overwrite the first.
        /// </summary>
        private static PurchaseInvoiceForm _open;

        /// <summary>Opens a new delivery from this company beside the app.</summary>
        public static void OpenAlongside(Supplier supplier, Form owner, Action onClosed = null)
            => Present(() => new PurchaseInvoiceForm(supplier), owner, onClosed);

        /// <summary>Reopens a delivery that was set aside, beside the app.</summary>
        public static void ResumeAlongside(PurchaseDrafts.Draft draft, Form owner, Action onClosed = null)
            => Present(() => Resume(draft), owner, onClosed);

        /// <summary>True while a delivery is being typed, whether or not its window is minimized.</summary>
        public static bool IsOpen => _open != null && !_open.IsDisposed;

        private static void Present(Func<PurchaseInvoiceForm> build, Form owner, Action onClosed)
        {
            if (!TryPresent(build, owner, onClosed))
                Msg.Info("هناك فاتورة مفتوحة بالفعل.\n" +
                         "أكمل إدخالها أو احفظها كمسودة قبل فتح فاتورة أخرى.");
        }

        /// <summary>
        /// Opens the delivery, or surfaces the one already open and returns false. The telling apart
        /// is kept out of <see cref="Present"/> so it can be exercised without a message box in the
        /// way — the box is the part that cannot be tested, not the rule.
        /// </summary>
        private static bool TryPresent(Func<PurchaseInvoiceForm> build, Form owner, Action onClosed)
        {
            if (IsOpen)
            {
                // Minimized is the likely case: the pharmacist put it aside, served someone, and has
                // now clicked "new invoice" having forgotten it. Bring back what they already typed.
                if (_open.WindowState == FormWindowState.Minimized)
                    _open.WindowState = FormWindowState.Normal;
                _open.Activate();
                return false;
            }

            PurchaseInvoiceForm form = build();

            // Owned by the MAIN window, not by whatever dialog opened this. A listing that opened a
            // delivery is itself modal and closes straight after, and a window owned by a form that
            // has closed goes with it — taking the pharmacist's typing along.
            Form root = owner;
            while (root != null && root.Owner != null) root = root.Owner;
            form.Owner = root;

            form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowInTaskbar = true;      // so a minimized delivery is findable from the taskbar
            form.FormClosed += (s, e) =>
            {
                _open = null;
                if (onClosed != null) onClosed();
            };

            _open = form;
            form.Show();
            return true;
        }

        /// <summary>
        /// Shuts the open delivery without asking. Logging out only: the drafts are cleared a moment
        /// later anyway, so offering to keep this one would be offering something that cannot survive.
        /// </summary>
        public static void CloseOpen()
        {
            if (!IsOpen) return;
            PurchaseInvoiceForm form = _open;
            _open = null;
            // Belt and braces. Closing in code does not prompt anyway — OnFormClosing only asks a user
            // who clicked the X — but saying it here means a later change there cannot start putting a
            // question in front of someone who is already on their way out.
            form._committed = true;
            form.Close();
        }

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
            // A delivery being typed can be minimized and come back untouched; a correction cannot,
            // because it is shown modally over the listing it was opened from (see OpenAlongside).
            MinimizeBox = !correcting;
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

                // Items rather than DataSource. A DataSource is only realised once the control has a
                // binding context, and a combo that has not been put on the form yet has none — so the
                // list stayed empty, selecting the invoice's own company threw on an empty list, and
                // correcting ANY invoice died inside the "تعذّر فتح الفاتورة" handler that wraps it.
                _company.Items.AddRange(companies.ToArray());
                _company.SelectedItem = companies.FirstOrDefault(c => c.Id == editing.SupplierId);
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

            // Setting a delivery aside is a deliberate act with its own button, as well as what
            // happens if the screen is simply closed. A customer at the counter will not wait while
            // the pharmacist hunts for the right way out.
            if (!correcting)
                bar.Controls.Add(Theme.ActionButton("حفظ كمسودة وإغلاق", SetAsideAndClose, width: 180));

            bar.Controls.Add(Theme.ActionButton("إلغاء", Cancel, width: 130));

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

                // On the books now, so the draft it came from is no longer waiting for anyone.
                _committed = true;
                if (_draftNumber > 0) PurchaseDrafts.Remove(_draftNumber);

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر حفظ الفاتورة: " + ex.Message); }
        }

        // ---------------- drafts ----------------

        /// <summary>Pours a set-aside delivery back into the screen.</summary>
        private void LoadDraft(PurchaseDrafts.Draft draft)
        {
            _draftNumber = draft.Number;
            _representative.Text = draft.Representative ?? "";
            _invoiceNumber.Text = draft.InvoiceNumber ?? "";
            _date.Value = draft.InvoiceDate == default(DateTime) ? DateTime.Today : draft.InvoiceDate;

            _lines.Clear();
            _lines.AddRange(draft.Lines);
            Text = "فاتورة مشتريات — مسودة " + draft.Number;
            Reload();
        }

        /// <summary>Records the screen as it stands. Returns the draft, or null when there is nothing
        /// worth keeping.</summary>
        private PurchaseDrafts.Draft SetAside()
        {
            if (_editing != null || _lines.Count == 0) return null;

            PurchaseDrafts.Draft draft = PurchaseDrafts.Save(
                _draftNumber, _supplier, _representative.Text, _invoiceNumber.Text, _date.Value.Date, _lines);
            _draftNumber = draft.Number;
            return draft;
        }

        private void SetAsideAndClose()
        {
            PurchaseDrafts.Draft draft = SetAside();
            if (draft == null) { Msg.Info("لا توجد أصناف لحفظها كمسودة."); return; }

            _committed = true;          // already kept; closing must not ask again
            Msg.Info("تم حفظ الفاتورة كمسودة " + draft.Number + ".\n" +
                     "للعودة إليها: الموردون والمشتريات ← المسودات.");
            DialogResult = DialogResult.Cancel;
            Close();
        }

        /// <summary>
        /// Settles what happens to typing that was never filed. Returns false to stay on the invoice.
        ///
        /// Both ways out of this screen — the إلغاء button and the window's X — come through here, so
        /// they ask the same question and mean the same thing. Closing is the deliberate act now that
        /// the window can be minimized: a pharmacist who only wants the invoice out of the way for a
        /// minute has a button for exactly that, so being asked here is not in anyone's way.
        /// </summary>
        private bool ResolveUnsaved()
        {
            if (_committed || _editing != null || _lines.Count == 0) return true;

            DialogResult answer = Msg.Ask(
                "الفاتورة بها " + _lines.Count + " صنف لم يُحفظ.\n\n" +
                "نعم = حفظ كمسودة والعودة إليها لاحقاً\n" +
                "لا = إلغاء الفاتورة وحذف ما تمّ إدخاله\n\n" +
                "(لإبقائها مفتوحة والعودة إليها بعد قليل استخدم زر التصغير.)",
                "إغلاق الفاتورة");

            if (answer == DialogResult.Cancel) return false;            // stay on the invoice

            if (answer == DialogResult.Yes)
            {
                PurchaseDrafts.Draft draft = SetAside();
                if (draft != null)
                    Msg.Info("تم حفظ الفاتورة كمسودة " + draft.Number + ".\n" +
                             "للعودة إليها: الموردون والمشتريات ← المسودات.");
            }
            else if (_draftNumber > 0)
            {
                PurchaseDrafts.Remove(_draftNumber);   // including the draft it was resumed from
            }

            _committed = true;
            return true;
        }

        /// <summary>The "إلغاء" button: offers to keep the typing rather than assuming it is rubbish.</summary>
        private void Cancel()
        {
            if (!ResolveUnsaved()) return;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only a deliberate close asks. On the way out of the whole program there is nothing to
            // offer — drafts live in memory and would not outlive it either — and a question nobody
            // is there to answer would just hang the shutdown.
            if (e.CloseReason == CloseReason.UserClosing && !ResolveUnsaved())
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
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
