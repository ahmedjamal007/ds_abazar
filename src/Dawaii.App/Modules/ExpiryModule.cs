using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>Near-expiry / expired batches list (FR-EXP-01) with dispose action (FR-EXP-03).</summary>
    public class ExpiryModule : ModuleControl
    {
        private NumericUpDown _window;
        private DataGridView _grid;
        private List<NearExpiryRow> _rows = new List<NearExpiryRow>();
        private Label _summary;

        public ExpiryModule()
        {
            var title = new Label { Text = "الأصناف قريبة الانتهاء", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.Add(new Label { Text = "خلال (يوم):", AutoSize = true, Margin = new Padding(6, 14, 4, 0), Font = Theme.Base(11f) });
            _window = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = 90, Width = 90, Margin = new Padding(4, 10, 4, 0), Font = Theme.Base(12f) };
            _window.ValueChanged += (s, e) => Reload();
            bar.Controls.Add(_window);
            if (Session.CanManageInventory) bar.Controls.Add(Btn("إتلاف الدفعات المحددة", DisposeSelected));
            bar.Controls.Add(Btn("تحديث", Reload));

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f) };
            if (Session.CanManageInventory)
            {
                var hint = new Label { Text = "اختر دفعة أو أكثر (Ctrl/Shift) ثم «إتلاف الدفعات المحددة».",
                    Dock = DockStyle.Top, Height = 20, ForeColor = Theme.TextMuted, Font = Theme.Base(9f) };
                Controls.Add(hint);
            }

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false,
                MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصنف", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الدفعة", DataPropertyName = "Batch", Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الكمية", DataPropertyName = "Qty", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصلاحية", DataPropertyName = "Expiry", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المتبقي (يوم)", DataPropertyName = "Days", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "القيمة", DataPropertyName = "Value", Width = 120 });
            _grid.CellFormatting += Colorize;

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(bar);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private void Reload()
        {
            try
            {
                _rows = Session.Services.Inventory.GetNearExpiry((int)_window.Value).ToList();
                _grid.DataSource = _rows.Select(r => new
                {
                    Id = r.Batch.Id,
                    Name = r.Item.DisplayName,
                    Batch = r.Batch.Id,
                    Qty = r.Batch.QuantityUnits,
                    Expiry = Fmt.Date(r.Batch.ExpiryDate),
                    Days = r.IsExpired ? "منتهي" : r.DaysUntilExpiry.ToString(),
                    Value = Fmt.Money(r.ValueAtRisk)
                }).ToList();

                int expired = _rows.Count(r => r.IsExpired);
                decimal atRisk = _rows.Sum(r => r.ValueAtRisk);
                _summary.Text = $"عدد الدفعات: {_rows.Count}  |  منتهية: {expired}  |  القيمة المعرضة: {Fmt.Money(atRisk)}";
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void Colorize(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            if (_rows[e.RowIndex].IsExpired) e.CellStyle.ForeColor = Theme.Danger;
            else if (_rows[e.RowIndex].DaysUntilExpiry <= 30) e.CellStyle.ForeColor = Theme.Accent;
        }

        private void DisposeSelected()
        {
            // Map the selected rows back by index into _rows (batch id is not a displayed column).
            var batchIds = _grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Index).Where(i => i >= 0 && i < _rows.Count)
                .Select(i => _rows[i].Batch.Id).Distinct().ToList();
            if (batchIds.Count == 0) { Msg.Info("اختر دفعة أو أكثر."); return; }
            if (!Msg.Confirm($"إتلاف {batchIds.Count} دفعة وإزالتها من المخزون؟ لا يمكن التراجع.")) return;

            int done = 0, skipped = 0;
            foreach (int id in batchIds)
            {
                // Dispose each independently: a batch already gone (its item was deleted elsewhere) or
                // already disposed is skipped, not treated as an error, and never aborts the rest.
                try
                {
                    if (Session.Services.Inventory.DisposeBatch(Session.CurrentUser, id)) done++;
                    else skipped++;
                }
                catch (DomainException ex) { Msg.Error(ex.Message); }
            }
            Reload();
            if (done > 0)
                Msg.Info(skipped > 0
                    ? $"تم إتلاف {done} دفعة، وتم تجاهل {skipped} دفعة لم تعد موجودة."
                    : $"تم إتلاف {done} دفعة.");
            else if (skipped > 0)
                Msg.Info("الدفعات المحددة لم تعد موجودة (رُبما حُذف الصنف). تم تحديث القائمة.");
        }

        private static Button Btn(string text, Action onClick)
            => Theme.ActionButton(text, onClick, width: 130);
    }
}
