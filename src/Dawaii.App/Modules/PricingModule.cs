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
    /// <summary>
    /// Price increases (V2.3 "زيادة الأسعار"): a multiplier over the current selling price, practical
    /// rounding, and prices typed by hand — all previewed on the grid, then applied in one confirmed step.
    ///
    /// The screen keeps a set of PENDING changes rather than writing as it goes. That is what lets the
    /// user price one group at 1.3, another at 1.2, hand-correct a few, look at the whole result, and
    /// only then commit — or walk away having changed nothing. A pending price is shown beside the
    /// current one, in its own colour, with a word saying where it came from: calculated, calculated
    /// and rounded, or typed. A drug whose price was typed on an earlier visit is marked يدوي and a
    /// multiplier leaves it alone unless the box to include such items is ticked.
    ///
    /// The same people who set prices by entering a delivery — the manager and a موظف ذو امتيازات —
    /// reach this screen; the service refuses anyone else regardless of what the sidebar shows.
    /// </summary>
    public class PricingModule : ModuleControl
    {
        /// <summary>A change the user has built but not yet applied.</summary>
        private class Pending
        {
            public PricePlanFigures Figures;
            public decimal? Multiplier;      // null when typed by hand
            public bool Manual;
            public bool BelowCost;           // lands under the cost on file — shown, not refused
        }

        /// <summary>The grid's row model. Named (not anonymous) so cell formatting can read it back.</summary>
        private class PriceGridRow
        {
            public bool Selected { get; set; }
            public int Id { get; set; }
            public string Name { get; set; }
            public string Packaging { get; set; }
            public string CostBox { get; set; }
            public string CurrentBox { get; set; }
            public string CurrentStrip { get; set; }
            public string NewBox { get; set; }
            public string NewStrip { get; set; }
            public string Multiplier { get; set; }
            public string Status { get; set; }
            public bool HasPending { get; set; }
            public bool PendingManual { get; set; }
            public bool NoPrice { get; set; }
            public bool BelowCost { get; set; }
            public bool ItemManual { get; set; }
        }

        private TextBox _search;
        private Timer _searchDebounce;
        private ComboBox _filter;
        private NumericUpDown _multiplier;
        private CheckBox _includeManual;
        private Label _summary, _rounding;
        private PillButton _apply;
        private DataGridView _grid;
        private CheckBox _selectAll;
        private bool _updatingSelection;

        private List<Item> _rows = new List<Item>();
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();

        public PricingModule()
        {
            var title = new Label { Text = "زيادة الأسعار", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            var hint = new Label
            {
                Text = "حدّد الأصناف، أدخل معامل الزيادة (يُضرب في سعر البيع الحالي) واضغط \"احتساب\" للمعاينة. لا يُحفظ شيء قبل الضغط على \"تطبيق\".",
                Dock = DockStyle.Top, Height = 24, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f)
            };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _searchDebounce = new Timer { Interval = 250 };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); Reload(); };
            _search.TextChanged += (s, e) => { _searchDebounce.Stop(); _searchDebounce.Start(); };

            // ---- the multiplier row
            var calc = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            calc.Controls.Add(new Label { Text = "معامل الزيادة:", AutoSize = true, Margin = new Padding(6, 12, 4, 0), Font = Theme.Base(11.5f) });
            _multiplier = new NumericUpDown
            {
                Minimum = 0.01m, Maximum = 100m, DecimalPlaces = 2, Increment = 0.05m, Value = 1.30m,
                Width = 100, Font = Theme.Base(12.5f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center,
                Margin = new Padding(4, 6, 4, 0)
            };
            calc.Controls.Add(_multiplier);
            calc.Controls.Add(Theme.ActionButton("احتساب للمحدد", PreviewSelected, primary: true, width: 150));
            _includeManual = new CheckBox { Text = "تضمين الأسعار اليدوية", AutoSize = true, Margin = new Padding(12, 14, 6, 0), Font = Theme.Base(11f) };
            calc.Controls.Add(_includeManual);
            _rounding = new Label { AutoSize = true, Margin = new Padding(16, 14, 6, 0), Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted };
            calc.Controls.Add(_rounding);

            // ---- the actions row
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
            _apply = (PillButton)Theme.ActionButton("تطبيق التغييرات", ApplyPending, primary: true, width: 190);
            actions.Controls.Add(_apply);
            actions.Controls.Add(Theme.ActionButton("تعديل يدوي", EditManually, width: 130));
            actions.Controls.Add(Theme.ActionButton("إلغاء تغيير المحدد", DiscardSelected, width: 160));
            actions.Controls.Add(Theme.ActionButton("إلغاء الكل", DiscardAll, width: 110));
            actions.Controls.Add(Theme.ActionButton("تحديث", Reload, width: 100));

            _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Font = Theme.Base(11f), Margin = new Padding(12, 8, 6, 0) };
            _filter.Items.AddRange(new object[] { "كل الأصناف", "بدون سعر", "أسعار يدوية", "لديها تغيير معلّق" });
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += (s, e) => Reload();
            actions.Controls.Add(_filter);

            _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, Font = Theme.Base(11f) };

            // ---- the grid
            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "", DataPropertyName = "Selected", Width = 36, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            Col("الصنف", "Name", 0, fill: true);
            Col("التعبئة", "Packaging", 80);
            Col("تكلفة العلبة", "CostBox", 115);
            Col("العلبة الآن", "CurrentBox", 115);
            Col("الشريط الآن", "CurrentStrip", 105);
            Col("العلبة الجديدة", "NewBox", 120);
            Col("الشريط الجديد", "NewStrip", 110);
            Col("المعامل", "Multiplier", 75);
            Col("الحالة", "Status", 200);
            _grid.CellFormatting += ColourRow;
            _grid.CellDoubleClick += (s, e) =>
            {
                // The row under the mouse, whatever is ticked elsewhere — a double-click is an answer
                // to "this one", not to "the selection".
                if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is PriceGridRow row) EditManually(row.Id);
            };
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) SetHeaderSelectionState(); };
            _grid.ColumnWidthChanged += (s, e) => PositionHeaderCheckBox();
            _grid.Resize += (s, e) => PositionHeaderCheckBox();

            _selectAll = new CheckBox { AutoSize = false, Text = "", TabStop = false };
            _selectAll.CheckedChanged += (s, e) => { if (!_updatingSelection) SetAllSelected(_selectAll.Checked); };
            _grid.Controls.Add(_selectAll);

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(actions);
            Controls.Add(calc);
            Controls.Add(_search);
            Controls.Add(hint);
            Controls.Add(title);
        }

        public override void OnActivated()
        {
            decimal step = Session.Services.Pricing.RoundingStep;
            _rounding.Text = "التقريب: " + (step > 0m ? "لأقرب " + step.ToString("0.##") : "تلقائي حسب السعر");
            Reload();
            _search.Focus();
        }

        // ---------------- listing ----------------

        private void Reload()
        {
            try
            {
                _rows = Session.Services.Pricing.Search(Session.CurrentUser, _search.Text).ToList();
                _rows = ApplyFilter(_rows);
                _grid.DataSource = _rows.Select(ToRow).ToList();
                SetHeaderSelectionState();
                BeginInvoke((Action)PositionHeaderCheckBox);
                RefreshSummary();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private List<Item> ApplyFilter(List<Item> items)
        {
            switch (_filter.SelectedIndex)
            {
                case 1: return items.Where(i => !i.SellingPrice.HasValue).ToList();
                case 2: return items.Where(i => i.ManualPrice).ToList();
                case 3: return items.Where(i => _pending.ContainsKey(i.Id)).ToList();
                default: return items;
            }
        }

        private PriceGridRow ToRow(Item i)
        {
            _pending.TryGetValue(i.Id, out Pending p);
            bool noCost = i.PurchasePrice <= 0m;
            bool noPrice = !i.SellingPrice.HasValue || i.SellingPrice.Value <= 0m;

            string status;
            if (p != null)
            {
                status = p.Manual ? "يدوي" : p.Figures.WasRounded ? "محسوب ومقرّب" : "محسوب";
                if (p.BelowCost) status += " ⚠ أقل من التكلفة";
                status += " — معلّق";
            }
            else if (noPrice) status = "بدون سعر — يُسعَّر عند أول توريد";
            else if (i.ManualPrice) status = "يدوي";
            else status = "";

            return new PriceGridRow
            {
                Selected = false,
                Id = i.Id,
                Name = i.DisplayName,
                Packaging = i.StripsPerBox + "×" + i.UnitsPerStrip,
                CostBox = noCost ? "—" : Fmt.Money(i.PurchasePrice * i.UnitsPerBox),
                CurrentBox = i.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(i, UnitType.Box)) : "—",
                CurrentStrip = i.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(i, UnitType.Strip)) : "—",
                NewBox = p != null ? Fmt.Money(p.Figures.BoxPrice) : "",
                NewStrip = p != null ? Fmt.Money(p.Figures.StripPrice) : "",
                Multiplier = p?.Multiplier?.ToString("0.00") ?? "",
                Status = status,
                HasPending = p != null,
                PendingManual = p != null && p.Manual,
                NoPrice = noPrice,
                BelowCost = p != null && p.BelowCost,
                ItemManual = i.ManualPrice
            };
        }

        /// <summary>Pending prices read as a different colour from current ones; typed ones as a third;
        /// items that cannot be priced are dimmed. The colour is the whole point of a preview.</summary>
        private void ColourRow(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as PriceGridRow;
            if (row == null) return;

            string col = _grid.Columns[e.ColumnIndex].DataPropertyName;
            bool isNewCol = col == "NewBox" || col == "NewStrip" || col == "Multiplier" || col == "Status";

            if (row.NoPrice) e.CellStyle.ForeColor = Theme.TextMuted;
            if (row.HasPending && isNewCol)
            {
                e.CellStyle.BackColor = row.PendingManual ? Color.FromArgb(255, 244, 222) : Color.FromArgb(226, 243, 236);
                e.CellStyle.ForeColor = row.BelowCost ? Theme.Danger : row.PendingManual ? Color.FromArgb(160, 90, 0) : Theme.PrimaryDark;
                e.CellStyle.Font = Theme.Base(11f, FontStyle.Bold);
            }
            else if (row.ItemManual && col == "Status")
            {
                e.CellStyle.ForeColor = Color.FromArgb(160, 90, 0);
            }
        }

        private void RefreshSummary()
        {
            int manual = _pending.Values.Count(p => p.Manual);
            int calc = _pending.Count - manual;
            _summary.Text = "الأصناف المعروضة: " + _rows.Count +
                            "     تغييرات معلّقة: " + _pending.Count +
                            (calc > 0 ? " (محسوبة " + calc + ")" : "") +
                            (manual > 0 ? " (يدوية " + manual + ")" : "");
            _apply.Text = _pending.Count > 0 ? "تطبيق التغييرات (" + _pending.Count + ")" : "تطبيق التغييرات";
        }

        // ---------------- building the plan ----------------

        private List<int> SelectedIds()
            => _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow && Convert.ToBoolean(r.Cells["Selected"].Value ?? false))
                .Select(r => ((PriceGridRow)r.DataBoundItem).Id)
                .ToList();

        /// <summary>The row the user is on when nothing is ticked, so a single drug needs no checkbox.</summary>
        private List<int> SelectedOrCurrentIds()
        {
            List<int> ids = SelectedIds();
            if (ids.Count == 0 && _grid.CurrentRow?.DataBoundItem is PriceGridRow row) ids.Add(row.Id);
            return ids;
        }

        private void PreviewSelected()
        {
            List<int> ids = SelectedOrCurrentIds();
            if (ids.Count == 0) { Msg.Info("حدّد صنفاً واحداً على الأقل."); return; }

            try
            {
                var plan = Session.Services.Pricing.Preview(Session.CurrentUser, ids, _multiplier.Value, _includeManual.Checked);

                int planned = 0, noPrice = 0, manualSkipped = 0, unchanged = 0, keptManual = 0, belowCost = 0;
                foreach (PricePlanRow r in plan)
                {
                    switch (r.Status)
                    {
                        case PricePlanStatus.Planned:
                            // A price the user typed on THIS visit is not overwritten by a multiplier
                            // unless they said to include manual prices — the same rule as stored ones.
                            if (_pending.TryGetValue(r.Item.Id, out Pending existing) && existing.Manual && !_includeManual.Checked)
                            {
                                keptManual++;
                                break;
                            }
                            _pending[r.Item.Id] = new Pending { Figures = r.New, Multiplier = r.Multiplier, Manual = false, BelowCost = r.BelowCost };
                            planned++;
                            if (r.BelowCost) belowCost++;
                            break;
                        case PricePlanStatus.NoPrice: noPrice++; break;
                        case PricePlanStatus.ManualSkipped: manualSkipped++; break;
                        case PricePlanStatus.Unchanged:
                            _pending.Remove(r.Item.Id);
                            unchanged++;
                            break;
                    }
                }

                Reload();

                string report = "تم احتساب " + planned + " سعراً: سعر البيع الحالي × " + _multiplier.Value.ToString("0.00") + ".";
                if (belowCost > 0)
                    report += "\n⚠ " + belowCost + " صنف سيصبح سعره أقل من تكلفة الشراء — راجعه قبل التطبيق.";
                if (unchanged > 0) report += "\n" + unchanged + " صنف عند هذا السعر بالفعل.";
                if (manualSkipped + keptManual > 0)
                    report += "\n" + (manualSkipped + keptManual) + " صنف بسعر يدوي تُرك كما هو (فعّل \"تضمين الأسعار اليدوية\" لإعادة احتسابه).";
                if (noPrice > 0) report += "\n" + noPrice + " صنف بدون سعر بيع — لا يوجد ما يُزاد عليه.";
                Msg.Info(report);
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void EditManually() => EditManually(null);

        private void EditManually(int? itemId)
        {
            if (!itemId.HasValue)
            {
                List<int> ids = SelectedOrCurrentIds();
                if (ids.Count != 1) { Msg.Info("اختر صنفاً واحداً للتعديل اليدوي."); return; }
                itemId = ids[0];
            }

            Item item = _rows.FirstOrDefault(i => i.Id == itemId.Value);
            if (item == null) return;
            _pending.TryGetValue(item.Id, out Pending existing);

            using (var dlg = new PriceEditForm(item, existing?.Figures))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Result == null) return;
                _pending[item.Id] = new Pending { Figures = dlg.Result, Multiplier = null, Manual = true };
            }
            Reload();
        }

        private void DiscardSelected()
        {
            List<int> ids = SelectedOrCurrentIds();
            int removed = ids.Count(id => _pending.Remove(id));
            if (removed == 0) { Msg.Info("لا توجد تغييرات معلّقة على الأصناف المحددة."); return; }
            Reload();
        }

        private void DiscardAll()
        {
            if (_pending.Count == 0) return;
            if (!Msg.Confirm("إلغاء " + _pending.Count + " تغييراً معلّقاً دون حفظ؟")) return;
            _pending.Clear();
            Reload();
        }

        // ---------------- applying ----------------

        private void ApplyPending()
        {
            if (_pending.Count == 0) { Msg.Info("لا توجد تغييرات معلّقة."); return; }

            int manual = _pending.Values.Count(p => p.Manual);
            int calc = _pending.Count - manual;
            string groups = string.Join("، ",
                _pending.Values.Where(p => p.Multiplier.HasValue)
                    .GroupBy(p => p.Multiplier.Value)
                    .OrderBy(g => g.Key)
                    .Select(g => g.Count() + " صنف × " + g.Key.ToString("0.00")));
            int belowCostPending = _pending.Values.Count(p => p.BelowCost);

            if (!Msg.Confirm(
                    "سيتم تغيير أسعار " + _pending.Count + " صنف بشكل دائم:\n" +
                    (calc > 0 ? "• محسوبة: " + groups + "\n" : "") +
                    (manual > 0 ? "• يدوية: " + manual + "\n" : "") +
                    (belowCostPending > 0 ? "⚠ " + belowCostPending + " منها أقل من التكلفة\n" : "") +
                    "\nسيظهر السعر الجديد فوراً في نقطة البيع. متابعة؟"))
                return;

            var changes = _pending.Select(kv => new PriceChange
            {
                ItemId = kv.Key,
                SellingPerUnit = kv.Value.Figures.UnitPrice,
                Manual = kv.Value.Manual
            }).ToList();

            try
            {
                PriceApplyResult result = Session.Services.Pricing.Apply(Session.CurrentUser, changes);

                // Whatever was written is no longer pending; whatever failed stays on screen to retry.
                foreach (PriceChange c in changes)
                    if (!result.Failures.Any(f => f.StartsWith("#" + c.ItemId + ":"))) _pending.Remove(c.ItemId);

                Reload();

                if (result.Failures.Count == 0)
                    Msg.Info("تم تحديث " + result.Applied + " سعراً.");
                else
                    Msg.Warn("تم تحديث " + result.Applied + " سعراً. تعذّر " + result.Failures.Count + ":\n" +
                             string.Join("\n", result.Failures.Take(8)));
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        // ---------------- select-all header checkbox (same pattern as الأصناف والمخزون) ----------------

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
            bool all = _grid.Rows.Count > 0 && _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow).All(r => Convert.ToBoolean(r.Cells["Selected"].Value ?? false));
            _updatingSelection = true;
            try { _selectAll.Checked = all; }
            finally { _updatingSelection = false; }
        }

        private void PositionHeaderCheckBox()
        {
            if (_selectAll == null || _grid.Columns.Count == 0) return;
            Rectangle header = _grid.GetCellDisplayRectangle(0, -1, true);
            _selectAll.Bounds = new Rectangle(header.X + (header.Width - 16) / 2, header.Y + (header.Height - 16) / 2, 16, 16);
            _selectAll.BringToFront();
        }

        private void Col(string header, string prop, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = prop, ReadOnly = true };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            _grid.Columns.Add(c);
        }
    }
}
