using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>Catalog + stock screen: search items, view live stock, manage items and batches.</summary>
    public class ItemsModule : ModuleControl
    {
        private TextBox _search;
        private ComboBox _filter;
        private DataGridView _grid;
        private CheckBox _selectAll;
        private bool _updatingSelection;
        private readonly Timer _searchDebounce = new Timer { Interval = 250 };
        private List<ItemStockView> _rows = new List<ItemStockView>();

        // A mutable binding row is required for the checkbox column. Anonymous objects expose
        // read-only properties, which makes DataGridView fail when a user changes Selected.
        private sealed class InventoryGridRow
        {
            public bool Selected { get; set; }
            public int Id { get; set; }
            public string Name { get; set; }
            public string BoxPrice { get; set; }
            public string StripPrice { get; set; }
            public int Available { get; set; }
            public string Expiry { get; set; }
            public string Status { get; set; }
        }

        // Client-side status filters over the loaded rows (V1.3).
        private static readonly string[] FilterLabels = { "الكل", "متاح", "مخزون منخفض", "نافد", "قرب الانتهاء" };

        public ItemsModule()
        {
            var title = new Label { Text = "الأصناف والمخزون", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); Reload(); };
            _search.TextChanged += (s, e) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
            _search.KeyDown += SearchKeyDown;
            var searchHint = new Label { Text = Session.CanManageInventory
                    ? "بحث بالاسم أو الرمز… (امسح رمزاً غير معروف لإنشاء صنف)"
                    : "بحث بالاسم أو الرمز… (للاطلاع فقط)",
                Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextMuted, Font = Theme.Base(9.5f) };

            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            filterRow.Controls.Add(new Label { Text = "تصفية:", AutoSize = true, Margin = new Padding(6, 10, 2, 0), Font = Theme.Base(11f) });
            _filter = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2, 6, 8, 0), Font = Theme.Base(11f) };
            _filter.Items.AddRange(FilterLabels);
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += (s, e) => Reload();
            filterRow.Controls.Add(_filter);

            // A plain cashier gets the screen read-only: they search stock and prices at the counter,
            // but only the manager and a "موظف ذو امتيازات" may change anything (V1.8).
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            if (Session.CanManageInventory)
            {
                toolbar.Controls.Add(Btn("صنف جديد", () => NewItem()));
                toolbar.Controls.Add(Btn("تعديل", EditItem));
                toolbar.Controls.Add(Btn("مخزون كامل", ReceiveFullStock));
                toolbar.Controls.Add(Btn("الدفعات", ShowBatches));
                toolbar.Controls.Add(Btn("الرموز / QR", ManageCodes));
                toolbar.Controls.Add(Btn("تفعيل/تعطيل", ToggleActive));
                toolbar.Controls.Add(Btn("إلغاء التحديد", () => SetAllSelected(false)));
                toolbar.Controls.Add(Btn("حذف المحدد", DeleteSelected));
            }
            toolbar.Controls.Add(Btn("تحديث", Reload));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false,
                MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Selected", HeaderText = "", DataPropertyName = "Selected", Width = 36,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الاسم", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "سعر العلبة", DataPropertyName = "BoxPrice", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "سعر الشريط", DataPropertyName = "StripPrice", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المتاح (حبة)", DataPropertyName = "Available", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "أقرب صلاحية", DataPropertyName = "Expiry", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الحالة", DataPropertyName = "Status", Width = 110 });
            _grid.CellFormatting += HighlightLowStock;
            _grid.CellDoubleClick += (s, e) => { if (Session.CanManageInventory) EditItem(); };
            _grid.CurrentCellDirtyStateChanged += GridCurrentCellDirtyStateChanged;
            _grid.CellValueChanged += GridCellValueChanged;
            _grid.ColumnWidthChanged += (s, e) => PositionHeaderCheckBox();
            _grid.Resize += (s, e) => PositionHeaderCheckBox();

            _selectAll = new CheckBox { AutoSize = false, Text = "", TabStop = false };
            _selectAll.CheckedChanged += (s, e) => { if (!_updatingSelection) SetAllSelected(_selectAll.Checked); };
            _grid.Controls.Add(_selectAll);

            Controls.Add(_grid);
            Controls.Add(filterRow);
            Controls.Add(toolbar);
            Controls.Add(_search);
            Controls.Add(searchHint);
            Controls.Add(title);
        }

        public override void OnActivated() { Reload(); _search.Focus(); }

        private void Reload()
        {
            try
            {
                _rows = Session.Services.Inventory.Search(_search.Text, limit: 200).ToList();
                _rows = ApplyFilter(_rows);
                _grid.DataSource = _rows.Select(v => new InventoryGridRow
                {
                    Selected = false,
                    Id = v.Item.Id,
                    Name = v.Item.DisplayName,
                    BoxPrice = v.Item.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(v.Item, UnitType.Box)) : "بدون سعر",
                    StripPrice = v.Item.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(v.Item, UnitType.Strip)) : "—",
                    Available = v.AvailableUnits,
                    Expiry = Fmt.Date(v.NearestExpiry),
                    Status = !v.Item.IsActive ? "معطّل" : v.IsLowStock ? "مخزون منخفض" : "متاح"
                }).ToList();
                SetHeaderSelectionState();
                BeginInvoke((Action)PositionHeaderCheckBox);
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private List<ItemStockView> ApplyFilter(List<ItemStockView> rows)
        {
            switch (_filter?.SelectedIndex ?? 0)
            {
                case 1: return rows.Where(v => v.AvailableUnits > 0).ToList();          // متاح
                case 2: return rows.Where(v => v.IsLowStock).ToList();                   // مخزون منخفض
                case 3: return rows.Where(v => v.AvailableUnits == 0).ToList();          // نافد
                case 4: return rows.Where(v => v.NearestExpiry.HasValue &&               // قرب الانتهاء
                                               v.NearestExpiry.Value <= DateTime.Today.AddDays(90)).ToList();
                default: return rows;                                                    // الكل
            }
        }

        private void ReceiveFullStock()
        {
            // Open directly: a selected row pre-fills the item; otherwise it's a blank "new item + stock"
            // form so the user types the drug and its stock together in one step (V1.3).
            using (var dlg = new FullStockForm(SelectedItem()))
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Reload();
        }

        private void HighlightLowStock(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            var v = _rows[e.RowIndex];
            if (!v.Item.IsActive)
                e.CellStyle.ForeColor = Theme.TextMuted;
            else if (v.IsLowStock)
                e.CellStyle.ForeColor = Theme.Danger;
        }

        private Item SelectedItem()
        {
            // Prefer the current row; fall back to a single ticked checkbox row. Map back to the item by
            // its Id (carried on the bound InventoryGridRow) rather than by list position, so the right
            // item is edited even if the grid order and _rows ever diverge.
            DataGridViewRow row = _grid.CurrentRow;
            if (row == null || row.IsNewRow)
            {
                var ticked = _grid.Rows.Cast<DataGridViewRow>()
                    .Where(r => !r.IsNewRow && Convert.ToBoolean(r.Cells["Selected"].Value ?? false))
                    .ToList();
                row = ticked.Count == 1 ? ticked[0] : null;
            }
            if (row == null || row.IsNewRow) return null;

            int rowId = Convert.ToInt32((row.DataBoundItem as InventoryGridRow)?.Id
                                        ?? (_rows.ElementAtOrDefault(row.Index)?.Item.Id ?? 0));
            return _rows.FirstOrDefault(v => v.Item.Id == rowId)?.Item;
        }

        private void GridCurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void GridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _grid.Columns["Selected"].Index)
                SetHeaderSelectionState();
        }

        private void SetAllSelected(bool selected)
        {
            if (_grid.Rows.Count == 0) return;
            _updatingSelection = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                    if (!row.IsNewRow) row.Cells["Selected"].Value = selected;
                _selectAll.Checked = selected;
            }
            finally { _updatingSelection = false; }
        }

        private void SetHeaderSelectionState()
        {
            if (_selectAll == null) return;
            bool allSelected = _grid.Rows.Count > 0 && _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow)
                .All(r => Convert.ToBoolean(r.Cells["Selected"].Value ?? false));
            _updatingSelection = true;
            try { _selectAll.Checked = allSelected; }
            finally { _updatingSelection = false; }
        }

        private void PositionHeaderCheckBox()
        {
            if (_selectAll == null || _grid.Columns.Count == 0) return;
            Rectangle header = _grid.GetCellDisplayRectangle(_grid.Columns["Selected"].Index, -1, true);
            _selectAll.Bounds = new Rectangle(header.X + (header.Width - 16) / 2, header.Y + (header.Height - 16) / 2, 16, 16);
            _selectAll.BringToFront();
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            string term = _search.Text.Trim();
            if (term.Length == 0) return;

            // FR-QRC-03: a known code selects its item. FR-QRC-04: an unknown code offers item creation.
            int? id = Session.Services.Codes.ResolveItemId(term);
            if (id.HasValue)
            {
                var v = _rows.FirstOrDefault(x => x.Item.Id == id.Value);
                if (v == null) { _search.Text = Session.Services.Items.GetById(id.Value)?.NameEn ?? term; return; }
                SelectRow(id.Value);
                return;
            }
            // Unknown code -> offer to create an item with the code pre-filled.
            if (!Session.CanManageInventory) { Msg.Info("رمز غير معروف. أضِف الصنف عبر المدير."); return; }
            if (!Msg.Confirm($"الرمز \"{term}\" غير معروف.\nهل تريد إنشاء صنف جديد بهذا الرمز؟")) return;
            NewItem(term);
        }

        private void SelectRow(int itemId)
        {
            int idx = _rows.FindIndex(v => v.Item.Id == itemId);
            if (idx >= 0 && idx < _grid.Rows.Count)
            {
                _grid.Rows[idx].Selected = true;
                _grid.CurrentCell = _grid.Rows[idx].Cells[0];
            }
        }

        private void NewItem(string prefillCode = null)
        {
            using (var dlg = new ItemForm { PrefillCode = prefillCode })
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    TryRun(() =>
                    {
                        int id = Session.Services.Inventory.CreateItem(Session.CurrentUser, dlg.Result);
                        AssignCodeIfAny(id, dlg.EnteredCode);
                    });
        }

        private void ManageCodes()
        {
            var item = SelectedItem();
            if (item == null) { Msg.Info("اختر صنفاً."); return; }
            using (var f = new CodesForm(item)) f.ShowDialog(FindForm());
        }

        /// <summary>Gives the item the code typed on the item screen, replacing any code it had (V1.9 —
        /// one barcode per item). An empty box leaves the existing code alone.</summary>
        private void AssignCodeIfAny(int itemId, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try { Session.Services.Codes.SetCode(Session.CurrentUser, itemId, code); }
            catch (DomainException ex) { Msg.Warn("لم يُضف الرمز: " + ex.Message); }
        }

        private void EditItem()
        {
            var item = SelectedItem();
            if (item == null) { Msg.Info("اختر صنفاً."); return; }
            using (var dlg = new ItemForm(item))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Result == null) return;
                try
                {
                    // Edit the persisted item by its id (dlg.Result may be a detached copy of the row).
                    Item toSave = dlg.Result;
                    toSave.Id = item.Id;
                    Session.Services.Inventory.UpdateItemDetails(Session.CurrentUser, toSave);
                    AssignCodeIfAny(item.Id, dlg.EnteredCode);
                    Msg.Info("تم حفظ التعديلات.");
                    Reload();
                }
                catch (DomainException ex) { Msg.Error(ex.Message); }
                catch (Exception ex) { Msg.Error("تعذّر حفظ التعديلات: " + ex.Message); }
            }
        }

        private void ReceiveStock()
        {
            // Use the selected row if there is one; otherwise let the user pick/create the item here (V1.3).
            var item = ResolveTargetItem();
            if (item == null) return;
            using (var dlg = new ReceiveStockForm(item))
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    TryRun(() => Session.Services.Inventory.ReceiveStock(
                        Session.CurrentUser, item.Id, dlg.QuantityBoxes, dlg.Expiry,
                        dlg.BoxPurchasePrice, dlg.BoxSellingPrice, dlg.StripsPerBox, dlg.BatchNumber));
        }

        /// <summary>Returns the selected item, or opens the item picker so the user can search/create one.</summary>
        private Item ResolveTargetItem()
        {
            var item = SelectedItem();
            if (item != null) return item;
            using (var picker = new ItemPickerForm())
                return picker.ShowDialog(FindForm()) == DialogResult.OK ? picker.Selected : null;
        }

        private void ShowBatches()
        {
            var item = SelectedItem();
            if (item == null) { Msg.Info("اختر صنفاً."); return; }
            using (var dlg = new BatchesForm(item)) dlg.ShowDialog(FindForm());
            Reload();
        }

        private void ToggleActive()
        {
            var item = SelectedItem();
            if (item == null) { Msg.Info("اختر صنفاً."); return; }
            TryRun(() => Session.Services.Inventory.SetItemActive(Session.CurrentUser, item.Id, !item.IsActive));
        }

        /// <summary>Deletes the selected item(s). Items that have sales are kept (offered as deactivate).</summary>
        private void DeleteSelected()
        {
            _grid.EndEdit();
            var items = _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow && Convert.ToBoolean(r.Cells["Selected"].Value ?? false))
                .Select(r => r.Index).Where(i => i >= 0 && i < _rows.Count)
                .Select(i => _rows[i].Item).Distinct().ToList();
            if (items.Count == 0) { Msg.Info("اختر صنفاً أو أكثر."); return; }
            if (!Msg.Confirm($"حذف {items.Count} صنف نهائياً مع مخزونه؟ لا يمكن التراجع.")) return;

            int deleted = 0;
            var withSales = new List<Item>();
            try
            {
                foreach (Item it in items)
                {
                    if (Session.Services.Inventory.DeleteItem(Session.CurrentUser, it.Id)) deleted++;
                    else withSales.Add(it);
                }
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
            Reload();
            ReportDeleteResult(deleted, withSales);
        }

        /// <summary>Deletes the entire catalog (all items in the system, not just the listed rows).
        /// Items with sales are kept (offered as deactivate), same as single delete.</summary>
        private void DeleteAll()
        {
            if (!Msg.Confirm("حذف جميع الأصناف في النظام نهائياً مع مخزونها؟ لا يمكن التراجع.")) return;
            if (!Msg.Confirm("تأكيد أخير: سيتم حذف كامل قائمة الأصناف والمخزون. هل أنت متأكد؟")) return;

            int deleted = 0;
            IReadOnlyList<Item> withSales = new List<Item>();
            try { deleted = Session.Services.Inventory.DeleteAllItems(Session.CurrentUser, out withSales); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
            Reload();
            ReportDeleteResult(deleted, withSales.ToList());
        }

        /// <summary>Shared result dialog: reports the deleted count and offers to deactivate items
        /// that were kept because they have sales history.</summary>
        private void ReportDeleteResult(int deleted, List<Item> withSales)
        {
            if (withSales.Count > 0)
            {
                // Saying so matters: a kept item has already given up its barcode (V1.9 — the number
                // belongs to the product, so a replacement record can be scanned in under it). Answering
                // "لا" here leaves the drug active and sellable but no longer scannable, and until V2.3
                // nothing on screen mentioned that.
                bool deactivate = Msg.Confirm(
                    $"تم حذف {deleted}. لا يمكن حذف {withSales.Count} صنف لوجود فواتير مرتبطة بها.\n" +
                    "تم تحرير الباركود الخاص بها ليُستخدم مع أصناف بديلة.\n\n" +
                    "هل تريد تعطيلها بدلاً من الحذف؟ (اختيار \"لا\" يبقيها للبيع بالاسم بدون باركود)");
                if (deactivate)
                {
                    foreach (Item it in withSales)
                        try { Session.Services.Inventory.SetItemActive(Session.CurrentUser, it.Id, false); } catch { }
                    Reload();
                }
            }
            else if (deleted > 0) Msg.Info($"تم حذف {deleted} صنف.");
        }

        private void TryRun(Action action)
        {
            try { action(); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private static Button Btn(string text, Action onClick)
            => Theme.ActionButton(text, onClick, width: 130);
    }
}
