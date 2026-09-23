using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// The deliveries set aside, and the way back into them (V2.3.2).
    ///
    /// Deliberately plain: a pharmacist opens this because a customer interrupted them ten minutes ago
    /// and they want to carry on. So it lists what is waiting, and double-clicking a row reopens that
    /// invoice exactly as it was left.
    ///
    /// Nothing listed here exists in the database. A draft holds no stock, owes no company money, and
    /// changes no price — it is typing that has not been confirmed yet.
    /// </summary>
    public class PurchaseDraftsForm : BaseForm
    {
        private DataGridView _grid;
        private Label _note;

        public PurchaseDraftsForm()
        {
            Text = "مسودات فواتير المشتريات";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(820, 420);

            var title = new Label
            {
                Text = "فواتير قيد الإدخال", Dock = DockStyle.Top, Height = 44,
                Font = Theme.Title(16f), ForeColor = Theme.Primary,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            };

            _note = new Label
            {
                Dock = DockStyle.Top, Height = 40, Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0),
                Text = "المسودة مجرد إدخال لم يُحفظ: لا مخزون ولا مبلغ مستحق على الشركة حتى تُحفظ الفاتورة.\n" +
                       "تُحذف المسودات عند تسجيل الخروج."
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true,
                MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            Theme.StyleGrid(_grid);
            Col("المسودة", "Number", 90);
            Col("الشركة", "Company", 0, fill: true);
            Col("رقم الفاتورة", "InvoiceNumber", 130);
            Col("التاريخ", "Date", 110);
            Col("الأصناف", "Lines", 80);
            Col("الإجمالي", "Total", 130);
            Col("حُفظت", "SavedAt", 120);
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ResumeSelected(); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
            bar.Controls.Add(Theme.ActionButton("متابعة الفاتورة", ResumeSelected, primary: true, width: 160));
            bar.Controls.Add(Theme.ActionButton("حذف المسودة", DiscardSelected, width: 140));
            bar.Controls.Add(Theme.ActionButton("إغلاق", Close, width: 120));

            Controls.Add(_grid);
            Controls.Add(_note);
            Controls.Add(title);
            Controls.Add(bar);

            Reload();
        }

        private void Reload()
        {
            _grid.DataSource = PurchaseDrafts.All.Select(d => new
            {
                d.Number,
                Company = d.Supplier?.Name ?? "—",
                InvoiceNumber = string.IsNullOrWhiteSpace(d.InvoiceNumber) ? "—" : d.InvoiceNumber,
                Date = Fmt.Date(d.InvoiceDate),
                Lines = d.Lines.Count,
                Total = Fmt.Money(d.Total),
                SavedAt = d.SavedAt.ToString("HH:mm")
            }).ToList();

            if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
        }

        private PurchaseDrafts.Draft Selected()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            var all = PurchaseDrafts.All;
            return idx >= 0 && idx < all.Count ? all[idx] : null;
        }

        private void ResumeSelected()
        {
            PurchaseDrafts.Draft draft = Selected();
            if (draft == null) { Msg.Info("اختر مسودة."); return; }

            // If a delivery is already open, ResumeAlongside just brings it forward and says so — this
            // picker then has to stay put, or the user loses their place for nothing.
            bool refused = PurchaseInvoiceForm.IsOpen;

            try
            {
                // The invoice screen takes over from here: it will clear this draft if the delivery is
                // saved, or file it again if the pharmacist is interrupted a second time. It opens
                // modeless, so this picker closes rather than sit modal in front of the whole app.
                PurchaseInvoiceForm.ResumeAlongside(draft, this);
            }
            catch (DomainException ex) { Msg.Error(ex.Message); return; }
            catch (Exception ex) { Log.Error("Resume purchase draft", ex); Msg.Error("تعذّر فتح المسودة."); return; }

            if (!refused) Close();
        }

        private void DiscardSelected()
        {
            PurchaseDrafts.Draft draft = Selected();
            if (draft == null) { Msg.Info("اختر مسودة."); return; }
            if (!Msg.Confirm("حذف المسودة " + draft.Number + " (" + draft.Lines.Count + " صنف) نهائياً؟")) return;

            PurchaseDrafts.Remove(draft.Number);
            Reload();
            if (PurchaseDrafts.Count == 0) Close();
        }

        private void Col(string header, string prop, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            _grid.Columns.Add(c);
        }
    }
}
