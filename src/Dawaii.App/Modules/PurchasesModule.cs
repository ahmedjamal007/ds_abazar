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
    /// Purchases ("المشتريات", V1.4, Admin): the log of goods bought from people who come to sell —
    /// bags (أكياس) and other supplies. Add / delete entries, filter by period, print and export.
    /// </summary>
    public class PurchasesModule : ModuleControl
    {
        private ComboBox _period;
        private DateTimePicker _date;
        private DataGridView _grid;
        private Label _summary;
        private List<Purchase> _rows = new List<Purchase>();

        public PurchasesModule()
        {
            var title = Theme.PageHeader("المشتريات", "مشتريات الصيدلية من الموردين والباعة (أكياس ومستلزمات أخرى)");

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            bar.Controls.Add(new Label { Text = "الفترة:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(11f) });
            _period = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2, 10, 8, 0), Font = Theme.Base(11f) };
            _period.Items.AddRange(new object[] { "يومي", "أسبوعي (آخر 7 أيام)", "شهري" });
            _period.SelectedIndex = 0;
            _period.SelectedIndexChanged += (s, e) => Reload();
            bar.Controls.Add(_period);
            bar.Controls.Add(new Label { Text = "التاريخ:", AutoSize = true, Margin = new Padding(6, 14, 2, 0), Font = Theme.Base(11f) });
            _date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 130, Margin = new Padding(2, 10, 8, 0), Font = Theme.Base(12f) };
            _date.ValueChanged += (s, e) => Reload();
            bar.Controls.Add(_date);
            bar.Controls.Add(Theme.ActionButton("إضافة مشترى", AddPurchase, primary: true, width: 130));
            bar.Controls.Add(Theme.ActionButton("حذف المحدد", DeleteSelected, width: 110));

            var export = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            export.Controls.Add(Theme.ActionButton("تصدير Excel", () => Export(pdf: false), width: 115));
            export.Controls.Add(Theme.ActionButton("تصدير PDF", () => Export(pdf: true), width: 110));
            export.Controls.Add(Theme.ActionButton("طباعة", PrintCurrent, width: 90));

            _summary = new Label { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.TextPrimary, Font = Theme.Base(12f, FontStyle.Bold), Padding = new Padding(6, 4, 6, 0) };

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "البيان", DataPropertyName = "Description", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المورد", DataPropertyName = "Supplier", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الكمية", DataPropertyName = "Qty", Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "سعر الوحدة", DataPropertyName = "UnitPrice", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الإجمالي", DataPropertyName = "Amount", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "بواسطة", DataPropertyName = "By", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "التاريخ", DataPropertyName = "At", Width = 140 });

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(export);
            Controls.Add(bar);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private (DateTime From, DateTime To, string Label) Period()
        {
            DateTime d = _date.Value.Date;
            switch (_period.SelectedIndex)
            {
                case 1: return (d.AddDays(-6), d.AddDays(1), $"أسبوعي {d.AddDays(-6):yyyy-MM-dd} → {d:yyyy-MM-dd}");
                case 2:
                    var first = new DateTime(d.Year, d.Month, 1);
                    return (first, first.AddMonths(1), $"شهري {first:yyyy-MM}");
                default: return (d, d.AddDays(1), $"يومي {d:yyyy-MM-dd}");
            }
        }

        private void Reload()
        {
            try
            {
                var (from, to, label) = Period();
                _rows = Session.Services.Purchases.InRange(from, to).ToList();
                _grid.DataSource = _rows.Select(p => new
                {
                    Description = p.Description,
                    Supplier = p.SupplierName ?? "—",
                    Qty = p.Quantity,
                    UnitPrice = Fmt.Money(p.UnitPrice),
                    Amount = Fmt.Money(p.Amount),
                    By = p.UserName ?? ("#" + p.UserId),
                    At = p.CreatedAt.ToString("yyyy-MM-dd HH:mm")
                }).ToList();
                _summary.Text = $"{label}   |   عدد المشتريات: {_rows.Count}   |   إجمالي المصروف: {Fmt.Money(_rows.Sum(p => p.Amount))}";
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void AddPurchase()
        {
            using (var f = new PurchaseForm())
                if (f.ShowDialog(FindForm()) == DialogResult.OK) Reload();
        }

        private void DeleteSelected()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _rows.Count) { Msg.Info("اختر مشترى من القائمة."); return; }
            Purchase p = _rows[idx];
            if (!Msg.Confirm($"حذف المشترى «{p.Description}» بقيمة {Fmt.Money(p.Amount)}؟")) return;
            try
            {
                Session.Services.Purchases.DeletePurchase(Session.CurrentUser, p.Id);
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        // ---------- print / export ----------

        private static readonly string[] Headers =
            { "البيان", "المورد", "الكمية", "سعر الوحدة", "الإجمالي", "بواسطة", "التاريخ" };

        private List<string[]> ReportRows() => _rows.Select(p => new[]
        {
            p.Description, p.SupplierName ?? "", p.Quantity.ToString(),
            p.UnitPrice.ToString("0.00"), p.Amount.ToString("0.00"),
            p.UserName ?? ("#" + p.UserId), p.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        private string ReportTitle => "المشتريات — " + Period().Label;

        private void Export(bool pdf)
        {
            if (_rows.Count == 0) { Msg.Info("لا توجد بيانات للتصدير."); return; }
            string path = ReportExporter.SaveWithDialog(FindForm(), ReportTitle, Headers, ReportRows(), pdf);
            if (path != null) Msg.Info("تم حفظ التقرير:\n" + path);
        }

        private void PrintCurrent()
        {
            if (_rows.Count == 0) { Msg.Info("لا توجد بيانات للطباعة."); return; }
            ReportPrinter.Print(FindForm(), ReportTitle, Headers, ReportRows());
        }
    }
}
