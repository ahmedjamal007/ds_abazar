using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// The companies the pharmacy buys from (V2.1 "الموردون والمشتريات").
    ///
    /// The mirror image of العملاء والديون: there the list is who owes the pharmacy, here it is who the
    /// pharmacy owes. A row opens the company's account — every delivery it made, what each invoice
    /// still owes, and the running total across all of them.
    ///
    /// Buying stock is stockroom work, so the manager and a "موظف ذو امتيازات" both reach this screen;
    /// deleting a company stays the manager's, and the service refuses anyone else regardless.
    /// </summary>
    public class SuppliersModule : ModuleControl
    {
        private TextBox _search;
        private CheckBox _onlyOwed;
        private DataGridView _grid;
        private Label _summary;
        private PillButton _drafts;
        private List<Supplier> _rows = new List<Supplier>();

        public SuppliersModule()
        {
            var title = new Label { Text = "الموردون والمشتريات", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _search.TextChanged += (s, e) => Reload();

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            toolbar.Controls.Add(Btn("شركة جديدة", AddSupplier));
            // Deliveries set aside when a customer interrupted. The count is on the button so a
            // forgotten invoice is visible from the doorway rather than only if you go looking.
            _drafts = (PillButton)Theme.ActionButton("المسودات", OpenDrafts, width: 140);
            toolbar.Controls.Add(_drafts);
            toolbar.Controls.Add(Btn("فواتير الشركة", OpenSupplier));
            toolbar.Controls.Add(Btn("فاتورة جديدة", NewInvoice));
            toolbar.Controls.Add(Btn("تعديل الاسم", RenameSupplier));
            if (Session.IsAdmin) toolbar.Controls.Add(Btn("حذف الشركة", DeleteSupplier));

            _onlyOwed = new CheckBox { Text = "الشركات المستحقة فقط", AutoSize = true, Margin = new Padding(10, 14, 6, 0), Font = Theme.Base(11f) };
            _onlyOwed.CheckedChanged += (s, e) => Reload();
            toolbar.Controls.Add(_onlyOwed);
            toolbar.Controls.Add(Btn("تحديث", Reload));

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(11f) };

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الشركة", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "عدد الفواتير", DataPropertyName = "Invoices", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المستحق عليها", DataPropertyName = "Outstanding", Width = 170 });
            _grid.CellDoubleClick += (s, e) => OpenSupplier();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _rows[e.RowIndex].Outstanding > 0m)
                    e.CellStyle.ForeColor = Theme.Danger;
            };

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(toolbar);
            Controls.Add(_search);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        /// <summary>
        /// Opens the deliveries that were set aside. Nothing here has touched stock or money — a draft
        /// is only typing — so this is simply a way back to where the pharmacist left off.
        /// </summary>
        private void OpenDrafts()
        {
            if (PurchaseDrafts.Count == 0)
            {
                Msg.Info("لا توجد مسودات محفوظة.\n\n" +
                         "عند إغلاق فاتورة قيد الإدخال تُحفظ تلقائياً هنا حتى تعود إليها.");
                return;
            }

            using (var f = new PurchaseDraftsForm()) f.ShowDialog(FindForm());
            Reload();
        }

        private void RefreshDraftsButton()
        {
            if (_drafts == null) return;
            int n = PurchaseDrafts.Count;
            _drafts.Text = n == 0 ? "المسودات" : "المسودات (" + n + ")";
            _drafts.Outline = n == 0;
            _drafts.Invalidate();
        }

        private void Reload()
        {
            try
            {
                _rows = (_onlyOwed.Checked
                    ? Session.Services.Suppliers.WithOutstanding()
                    : Session.Services.Suppliers.Search(_search.Text)).ToList();

                if (_onlyOwed.Checked && !string.IsNullOrWhiteSpace(_search.Text))
                    _rows = _rows.Where(s => (s.Name ?? "").Contains(_search.Text.Trim())).ToList();

                _grid.DataSource = _rows.Select(s => new
                {
                    s.Name,
                    Invoices = s.InvoiceCount,
                    Outstanding = Fmt.Money(s.Outstanding)
                }).ToList();

                _summary.Text = "إجمالي المستحق للموردين: " + Fmt.Money(Session.Services.Suppliers.TotalOutstanding());
                RefreshDraftsButton();
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        /// <summary>The company behind the selected row. The id is not a displayed column, so the row
        /// index is mapped back into the list the grid was built from.</summary>
        private Supplier Selected()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
        }

        private void AddSupplier()
        {
            string name = Prompt.Show("اسم الشركة", "شركة جديدة");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { Session.Services.Suppliers.CreateSupplier(Session.CurrentUser, name); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void RenameSupplier()
        {
            Supplier s = Selected();
            if (s == null) { Msg.Info("اختر شركة."); return; }

            string name = Prompt.Show("اسم الشركة", "تعديل الاسم", s.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            try { Session.Services.Suppliers.RenameSupplier(Session.CurrentUser, s.Id, name); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        /// <summary>Deletes a company. One that has ever delivered is kept — its invoices are where the
        /// stock on the shelf came from — and one still owed money is refused outright by the service.</summary>
        private void DeleteSupplier()
        {
            Supplier s = Selected();
            if (s == null) { Msg.Info("اختر شركة."); return; }
            if (!Msg.Confirm("حذف الشركة \"" + s.Name + "\" نهائياً؟ لا يمكن التراجع.")) return;

            try
            {
                if (Session.Services.Suppliers.DeleteSupplier(Session.CurrentUser, s.Id)) Msg.Info("تم حذف الشركة.");
                else Msg.Warn("لا يمكن حذف هذه الشركة لوجود فواتير مسجلة باسمها.");
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void OpenSupplier()
        {
            Supplier s = Selected();
            if (s == null) { Msg.Info("اختر شركة."); return; }

            using (var f = new SupplierInvoicesForm(s.Id)) f.ShowDialog(FindForm());
            Reload();
        }

        private void NewInvoice()
        {
            Supplier s = Selected();
            if (s == null) { Msg.Info("اختر شركة."); return; }

            // Modeless: the delivery can be minimized while the customer who just walked in is served.
            // Reload runs when it closes, because there is no ShowDialog left to wait on — and only if
            // this screen is still alive, since the user may well have navigated away from it by then.
            PurchaseInvoiceForm.OpenAlongside(s, FindForm(), () => { if (!IsDisposed) Reload(); });
        }

        private static Button Btn(string text, Action onClick) => Theme.ActionButton(text, onClick);
    }
}
