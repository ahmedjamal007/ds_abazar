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
    /// Price changes (V2.3 "تعديل الأسعار"): a multiplier over the current selling price — raising it
    /// or lowering it — practical rounding, and prices typed by hand; all previewed on the grid, then
    /// applied in one confirmed step.
    ///
    /// This screen collects input and shows results. Every rule behind it — who may reprice, which
    /// multiplier suits which operation, whether a hand-set price is protected, how the figure rounds,
    /// whether it falls below cost, whether the stored price moved since the preview — belongs to
    /// <see cref="PricingService"/>, so another front end would behave identically.
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
        /// <summary>
        /// One calculated-but-not-yet-saved price. Everything here came from a
        /// <see cref="PricePlanRow"/> the service produced — the screen does not compute any of it.
        /// </summary>
        private class Pending
        {
            public PricePlanFigures Figures;
            public PriceOperation Operation;
            public decimal? Multiplier;      // null when typed by hand
            public bool BelowCost;           // lands under the cost on file — shown, not refused

            /// <summary>The item's stored per-unit price when this was calculated, so applying can tell
            /// whether another terminal moved it in the meantime.</summary>
            public decimal? PricedAt;

            public bool Manual => Operation == PriceOperation.Manual;
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
            public string Operation { get; set; }
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
        private Label _summary, _rounding, _meaning;
        private PillButton _apply;
        private DataGridView _grid;
        private CheckBox _selectAll;
        private bool _updatingSelection;

        private List<Item> _rows = new List<Item>();

        /// <summary>
        /// Prices calculated but not yet saved, and the items ticked to calculate them — both keyed by
        /// item id, NOT by grid row. The grid is rebuilt on every search, filter and preview, and a row
        /// object does not survive that; an id does. Selecting ten drugs and then previewing used to
        /// clear every tick, because the rebuilt rows all defaulted to unticked.
        ///
        /// They are session state, not screen state (V2.3.2). Navigating away disposes this module, so
        /// while these were instance fields a pharmacist who priced forty drugs and stepped over to the
        /// till to serve someone came back to an empty screen — the same loss of context that drafts
        /// fixed for deliveries. Cleared at logout by <see cref="ResetPending"/>, like the POS carts.
        ///
        /// Nothing here holds a live <see cref="Item"/>, so none of it goes stale while the user is
        /// away; and each pending price remembers what the price was when it was calculated, so the
        /// service still refuses to apply one that another terminal has moved in the meantime.
        /// </summary>
        private static readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private static readonly HashSet<int> _selected = new HashSet<int>();

        /// <summary>Forgets every unapplied price — called at logout, so the next user starts clean.</summary>
        public static void ResetPending() { _pending.Clear(); _selected.Clear(); }

        public PricingModule()
        {
            var title = new Label { Text = "تعديل الأسعار", Font = Theme.Title(20f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 44 };

            var hint = new Label
            {
                Text = "حدّد الأصناف، أدخل المعامل، ثم اضغط \"زيادة الأسعار\" أو \"تخفيض الأسعار\" للمعاينة. " +
                       "المعامل يُضرب في سعر البيع الحالي — 1.30 = زيادة 30%، 0.90 = تخفيض 10%. لا يُحفظ شيء قبل \"تطبيق التغييرات\".",
                Dock = DockStyle.Top, Height = 24, ForeColor = Theme.TextMuted, Font = Theme.Base(10.5f)
            };

            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(13f), Height = 32 };
            _searchDebounce = new Timer { Interval = 250 };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); Reload(); };
            _search.TextChanged += (s, e) => { _searchDebounce.Stop(); _searchDebounce.Start(); };

            // ---- the multiplier row
            var calc = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            calc.Controls.Add(new Label { Text = "المعامل:", AutoSize = true, Margin = new Padding(6, 12, 4, 0), Font = Theme.Base(11.5f) });
            _multiplier = new NumericUpDown
            {
                Minimum = 0.01m, Maximum = 100m, DecimalPlaces = 2, Increment = 0.05m, Value = 1.30m,
                Width = 100, Font = Theme.Base(12.5f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center,
                Margin = new Padding(4, 6, 4, 0)
            };
            calc.Controls.Add(_multiplier);

            // Two buttons, not one: the operation is a decision the user makes explicitly, so a
            // mistyped 0.90 can never quietly cut prices under a button that says "increase".
            calc.Controls.Add(Theme.ActionButton("زيادة الأسعار", () => RunOperation(PriceOperation.Increase), primary: true, width: 140));
            calc.Controls.Add(Theme.ActionButton("تخفيض الأسعار", () => RunOperation(PriceOperation.Decrease), width: 140));

            // What the number in the box actually means, updated as it is typed — "0.90" on its own
            // tells a cashier nothing.
            _meaning = new Label { AutoSize = true, Margin = new Padding(10, 14, 6, 0), Font = Theme.Base(10.5f, FontStyle.Bold) };
            calc.Controls.Add(_meaning);
            _multiplier.ValueChanged += (s, e) => ShowMultiplierMeaning();

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
            Col("العملية", "Operation", 90);
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
            _grid.CellValueChanged += (s, e) =>
            {
                if (_updatingSelection || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                if (_grid.Rows[e.RowIndex].DataBoundItem is PriceGridRow row)
                {
                    bool ticked = Convert.ToBoolean(_grid.Rows[e.RowIndex].Cells["Selected"].Value ?? false);
                    if (ticked) _selected.Add(row.Id); else _selected.Remove(row.Id);
                }
                SetHeaderSelectionState();
            };
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
            ShowMultiplierMeaning();
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
                // Selections follow the items, not the rows. An id that filtering has hidden is
                // dropped — "select all" then filter then act must not reach rows off screen — but an
                // id that is still visible keeps its tick through search, preview and refresh.
                var visible = new HashSet<int>(_rows.Select(i => i.Id));
                _selected.RemoveWhere(id => !visible.Contains(id));

                _updatingSelection = true;
                try { _grid.DataSource = _rows.Select(ToRow).ToList(); }
                finally { _updatingSelection = false; }

                SetHeaderSelectionState();
                BeginInvoke((Action)PositionHeaderCheckBox);
                RefreshSummary();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Log.Error("Pricing reload", ex); Msg.Error("تعذّر تحميل قائمة الأصناف."); }
        }

        /// <summary>Says what the multiplier in the box would do, so "0.90" is never read as "90".</summary>
        private void ShowMultiplierMeaning()
        {
            decimal m = _multiplier.Value;
            if (m == 1m)
            {
                _meaning.Text = "1.00 — لا تغيير";
                _meaning.ForeColor = Theme.TextMuted;
            }
            else if (m > 1m)
            {
                _meaning.Text = "↑ " + PriceOperations.Describe(PriceOperation.Increase, m);
                _meaning.ForeColor = Theme.PrimaryDark;
            }
            else
            {
                _meaning.Text = "↓ " + PriceOperations.Describe(PriceOperation.Decrease, m);
                _meaning.ForeColor = Color.FromArgb(160, 90, 0);
            }
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
                Selected = _selected.Contains(i.Id),
                Id = i.Id,
                Name = i.DisplayName,
                Packaging = i.StripsPerBox + "×" + i.UnitsPerStrip,
                CostBox = noCost ? "—" : Fmt.Money(UnitConverter.CostOf(i, UnitType.Box)),
                CurrentBox = i.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(i, UnitType.Box)) : "—",
                CurrentStrip = i.SellingPrice.HasValue ? Fmt.Money(UnitConverter.PriceOf(i, UnitType.Strip)) : "—",
                NewBox = p != null ? Fmt.Money(p.Figures.BoxPrice) : "",
                NewStrip = p != null ? Fmt.Money(p.Figures.StripPrice) : "",
                Operation = p != null ? PriceOperations.LabelAr(p.Operation) : "",
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
            bool isNewCol = col == "NewBox" || col == "NewStrip" || col == "Operation" ||
                            col == "Multiplier" || col == "Status";

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

        /// <summary>The ticked items that are currently on screen — the id set is the truth, and the
        /// grid is filtered to what the user can see.</summary>
        private List<int> SelectedIds()
            => _rows.Where(i => _selected.Contains(i.Id)).Select(i => i.Id).ToList();

        /// <summary>The row the user is on when nothing is ticked, so a single drug needs no checkbox.</summary>
        private List<int> SelectedOrCurrentIds()
        {
            List<int> ids = SelectedIds();
            if (ids.Count == 0 && _grid.CurrentRow?.DataBoundItem is PriceGridRow row) ids.Add(row.Id);
            return ids;
        }

        /// <summary>
        /// Previews an increase or a decrease. One path for both: the operation is passed to the
        /// service, which validates the multiplier against it and does the arithmetic — this screen
        /// only reports what came back, so the two buttons can never drift apart.
        /// </summary>
        private void RunOperation(PriceOperation operation)
        {
            List<int> ids = SelectedOrCurrentIds();
            if (ids.Count == 0) { Msg.Info("حدّد صنفاً واحداً على الأقل."); return; }

            try { Msg.Info(BuildPlan(operation, ids)); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Log.Error("Pricing preview", ex); Msg.Error("تعذّر احتساب الأسعار."); }
        }

        /// <summary>
        /// Does the work and returns what happened, showing nothing. Separate from
        /// <see cref="RunOperation"/> so the calculation can be exercised without a message box in the
        /// way, and so the whole report is assembled in one place.
        /// </summary>
        private string BuildPlan(PriceOperation operation, List<int> ids)
        {
            {
                // Which items are already holding a hand-typed pending price. The SERVICE decides what
                // that means; the screen only reports the state it is holding.
                var pendingManual = new HashSet<int>(_pending.Where(kv => kv.Value.Manual).Select(kv => kv.Key));

                IReadOnlyList<PricePlanRow> plan = Session.Services.Pricing.Preview(
                    Session.CurrentUser, ids, operation, _multiplier.Value, _includeManual.Checked, pendingManual);

                int planned = 0, noPrice = 0, manualSkipped = 0, unchanged = 0, belowCost = 0;
                foreach (PricePlanRow r in plan)
                {
                    switch (r.Status)
                    {
                        case PricePlanStatus.Planned:
                            _pending[r.Item.Id] = new Pending
                            {
                                Figures = r.New,
                                Operation = r.Operation,
                                Multiplier = r.Multiplier,
                                BelowCost = r.BelowCost,
                                PricedAt = r.PricedAt
                            };
                            planned++;
                            if (r.BelowCost) belowCost++;
                            break;
                        case PricePlanStatus.NoPrice: noPrice++; break;
                        case PricePlanStatus.ManualSkipped: manualSkipped++; break;
                        case PricePlanStatus.Unchanged:
                            // The figure landed back on the current price; that is not a change, and a
                            // stale pending row for it would be a lie.
                            _pending.Remove(r.Item.Id);
                            unchanged++;
                            break;
                    }
                }

                Reload();

                string verb = PriceOperations.LabelAr(operation);
                string report = "تم احتساب " + planned + " سعراً — " +
                                PriceOperations.Describe(operation, _multiplier.Value) +
                                " (سعر البيع الحالي × " + _multiplier.Value.ToString("0.00") + ").";
                if (belowCost > 0)
                    report += "\n⚠ " + belowCost + " صنف سيصبح سعره أقل من تكلفة الشراء — راجعه قبل التطبيق.";
                if (unchanged > 0) report += "\n" + unchanged + " صنف عند هذا السعر بالفعل (لم يُضَف تغيير).";
                if (manualSkipped > 0)
                    report += "\n" + manualSkipped + " صنف بسعر يدوي تُرك كما هو (فعّل \"تضمين الأسعار اليدوية\" لإعادة احتسابه).";
                if (noPrice > 0) report += "\n" + noPrice + " صنف بدون سعر بيع — لا يوجد ما يُطبَّق عليه " + verb + ".";
                return report;
            }
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
                PricePlanRow row = dlg.Result;
                _pending[item.Id] = new Pending
                {
                    Figures = row.New,
                    Operation = PriceOperation.Manual,
                    Multiplier = null,
                    BelowCost = row.BelowCost,        // decided by the service, for manual as for calculated
                    PricedAt = row.PricedAt
                };
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
            if (!Msg.Confirm(ConfirmationText())) return;

            var changes = _pending.Select(kv => new PriceChange
            {
                ItemId = kv.Key,
                SellingPerUnit = kv.Value.Figures.UnitPrice,
                Operation = kv.Value.Operation,
                Multiplier = kv.Value.Multiplier,
                // What the price was when this was calculated. The service refuses the write if another
                // terminal has moved it since, rather than erasing their change.
                ExpectedCurrentUnitPrice = kv.Value.PricedAt
            }).ToList();

            try
            {
                PriceApplyResult result = Session.Services.Pricing.Apply(Session.CurrentUser, changes);

                // Whatever was written is no longer pending; whatever failed stays on screen to retry.
                HashSet<int> failed = result.FailedItemIds;
                foreach (PriceChange c in changes)
                    if (!failed.Contains(c.ItemId)) _pending.Remove(c.ItemId);

                Reload();

                if (result.Failures.Count == 0)
                    Msg.Info("تم تحديث " + result.Applied + " سعراً.");
                else
                    Msg.Warn("تم تحديث " + result.Applied + " سعراً. تعذّر " + result.Failures.Count + " — تبقى معلّقة:\n" +
                             string.Join("\n", result.Failures.Take(8).Select(f => f.ToString())) +
                             (result.Failures.Count > 8 ? "\n…" : ""));
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Log.Error("Pricing apply", ex); Msg.Error("تعذّر حفظ الأسعار. لم يتم تغيير شيء."); }
        }

        /// <summary>
        /// Spells out exactly what is about to happen: which operation, how many items at which
        /// multiplier, how many were typed by hand, and how many would sell below cost. This is the
        /// last point at which the user can stop, so it names the operation rather than showing a bare
        /// number the reader has to interpret.
        /// </summary>
        private string ConfirmationText()
        {
            var sb = new System.Text.StringBuilder();
            var byOperation = _pending.Values
                .Where(p => p.Multiplier.HasValue)
                .GroupBy(p => p.Operation)
                .OrderBy(g => g.Key);

            foreach (var op in byOperation)
            {
                sb.Append(op.Key == PriceOperation.Increase ? "زيادة الأسعار" : "تخفيض الأسعار").Append(":\n");
                foreach (var byMultiplier in op.GroupBy(p => p.Multiplier.Value).OrderBy(g => g.Key))
                    sb.Append("• ").Append(byMultiplier.Count()).Append(" صنف × ")
                      .Append(byMultiplier.Key.ToString("0.00"))
                      .Append("  (").Append(PriceOperations.Describe(op.Key, byMultiplier.Key)).Append(")\n");
            }

            int manual = _pending.Values.Count(p => p.Manual);
            if (manual > 0) sb.Append("أسعار يدوية:\n• ").Append(manual).Append(" صنف\n");

            int belowCost = _pending.Values.Count(p => p.BelowCost);
            if (belowCost > 0) sb.Append("⚠ ").Append(belowCost).Append(" منها أقل من التكلفة\n");

            sb.Append("\nالإجمالي: ").Append(_pending.Count).Append(" صنف بشكل دائم.\n");
            sb.Append("سيظهر السعر الجديد فوراً في نقطة البيع. متابعة؟");
            return sb.ToString();
        }

        // ---------------- select-all header checkbox (same pattern as الأصناف والمخزون) ----------------

        /// <summary>
        /// Ticks or unticks everything CURRENTLY VISIBLE, and records it by id. Items hidden by the
        /// filter are deliberately untouched: "select all" while filtered to "أسعار يدوية" must not
        /// quietly arm every drug in the pharmacy.
        /// </summary>
        private void SetAllSelected(bool selected)
        {
            if (_grid.Rows.Count == 0) return;
            _updatingSelection = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    row.Cells["Selected"].Value = selected;
                    if (row.DataBoundItem is PriceGridRow r)
                    {
                        if (selected) _selected.Add(r.Id); else _selected.Remove(r.Id);
                    }
                }
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
