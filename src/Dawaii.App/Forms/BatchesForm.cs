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
    /// <summary>Shows an item's stock batches and lets an Admin edit, adjust or dispose them
    /// (FR-INV-04, FR-EXP-03). Each row shows the entered BOX prices next to the strip prices derived
    /// from them, so it is obvious the two can never disagree.</summary>
    public class BatchesForm : BaseForm
    {
        private readonly Item _item;
        private DataGridView _grid;

        public BatchesForm(Item item)
        {
            _item = item;
            BuildUi();
            Reload();
        }

        private void BuildUi()
        {
            Text = "دفعات المخزون: " + _item.NameEn;
            ClientSize = new Size(940, 440);
            StartPosition = FormStartPosition.CenterParent;

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft };
            toolbar.Controls.Add(Btn("تعديل الدفعة", EditBatch));
            toolbar.Controls.Add(Btn("تعديل كمية", Adjust));
            toolbar.Controls.Add(Btn("إتلاف الدفعة", DisposeSelected));
            toolbar.Controls.Add(Btn("إغلاق", () => Close()));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الدفعة", DataPropertyName = "Lot", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الكمية (حبة)", DataPropertyName = "Qty", Width = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصلاحية", DataPropertyName = "Expiry", Width = 105 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "أشرطة/علبة", DataPropertyName = "Strips", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "شراء العلبة", DataPropertyName = "BoxCost", Width = 105 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "شراء الشريط", DataPropertyName = "StripCost", Width = 105 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "بيع العلبة", DataPropertyName = "BoxSell", Width = 105 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "بيع الشريط", DataPropertyName = "StripSell", Width = 105 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الحالة", DataPropertyName = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            Controls.Add(_grid);
            Controls.Add(toolbar);
        }

        private List<StockBatch> _batches;

        private void Reload()
        {
            _batches = Session.Services.Stock.GetBatches(_item.Id, includeDisposed: true).ToList();
            _grid.DataSource = _batches.Select(b => new
            {
                b.Id,
                Lot = string.IsNullOrEmpty(b.BatchNumber) ? "#" + b.Id : b.BatchNumber,
                Qty = b.QuantityUnits,
                Expiry = Fmt.Date(b.ExpiryDate),
                Strips = b.StripsPerBox,
                BoxCost = Fmt.Money(b.BoxPurchasePrice),
                StripCost = Fmt.Money(b.StripPurchasePrice),
                BoxSell = Fmt.Money(b.BoxSellingPrice),
                StripSell = Fmt.Money(b.StripSellingPrice),
                Status = b.IsDisposed ? "متلَف" : "متاح"
            }).ToList();
        }

        private StockBatch Selected()
        {
            if (_grid.CurrentRow == null) return null;
            int index = _grid.CurrentRow.Index;
            return index >= 0 && index < _batches.Count ? _batches[index] : null;
        }

        /// <summary>Opens the batch in the entry form so its number, expiry, box prices and strips per box
        /// can be corrected. Saving re-derives the strip prices and, if this is the newest batch, moves the
        /// item's selling price with it.</summary>
        private void EditBatch()
        {
            var b = Selected();
            if (b == null) { Msg.Info("اختر دفعة."); return; }
            if (b.IsDisposed) { Msg.Info("لا يمكن تعديل دفعة متلَفة."); return; }

            using (var dlg = new ReceiveStockForm(_item, b))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    b.BatchNumber = dlg.BatchNumber;
                    b.ExpiryDate = dlg.Expiry;
                    b.StripsPerBox = dlg.StripsPerBox;
                    b.BoxPurchasePrice = dlg.BoxPurchasePrice;
                    b.BoxSellingPrice = dlg.BoxSellingPrice;
                    b.QuantityUnits = dlg.QuantityBoxes * BatchPricing.UnitsPerBox(dlg.StripsPerBox, b.UnitsPerStrip);
                    Session.Services.Inventory.UpdateBatch(Session.CurrentUser, b);
                    Reload();
                }
                catch (DomainException ex) { Msg.Error(ex.Message); }
                catch (Exception ex) { Msg.Error(ex.Message); }
            }
        }

        private void Adjust()
        {
            var b = Selected();
            if (b == null) { Msg.Info("اختر دفعة."); return; }
            string deltaStr = Prompt.Show("التغيير في الكمية (مثال: -5 للتلف، +3 لتصحيح)", "تعديل الكمية");
            if (string.IsNullOrWhiteSpace(deltaStr)) return;
            if (!int.TryParse(deltaStr.Trim(), out int delta) || delta == 0) { Msg.Warn("قيمة غير صالحة."); return; }
            string reason = Prompt.Show("سبب التعديل (إلزامي)", "السبب");
            try
            {
                Session.Services.Inventory.AdjustStock(Session.CurrentUser, b.Id, delta, reason);
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void DisposeSelected()
        {
            var b = Selected();
            if (b == null) { Msg.Info("اختر دفعة."); return; }
            if (b.IsDisposed) { Msg.Info("الدفعة متلَفة بالفعل."); return; }
            if (!Msg.Confirm($"إتلاف الدفعة {b.Id} ({b.QuantityUnits} حبة)؟")) return;
            try
            {
                Session.Services.Inventory.DisposeBatch(Session.CurrentUser, b.Id);
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private static Button Btn(string text, Action onClick)
            => Theme.ActionButton(text, onClick, width: 140);
    }
}
