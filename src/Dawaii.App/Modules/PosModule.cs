using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>Point of sale (FR-POS-*). Fully keyboard-operable: F2 search, F9 cash, F10 credit, Del
    /// removes; every cart line also carries a ✕ button for removing it with the mouse.</summary>
    public class PosModule : ModuleControl
    {
        private class Row { public Item Item; public UnitType Unit = UnitType.Box; public int Qty = 1;
            public decimal UnitPrice => UnitConverter.PriceOf(Item, Unit);
            public decimal Total => UnitConverter.LineTotal(Item, Qty, Unit); }

        // Box, strip or single tablet. V1.2 req 3 had dropped the single unit; V1.9 puts it back —
        // customers do buy loose tablets, and the price of one is just the box price divided down
        // (the whole engine — pricing, FEFO, receipts, returns — always kept working in single units).
        private static readonly string[] UnitLabels = { "علبة", "شريط", "حبة" };

        // ---------------- multiple active invoices (V2.3) ----------------
        //
        // A customer who is still deciding must not hold up the next one in the queue. So the POS
        // keeps several carts and shows ONE of them: everything the screen edits — lines, credit
        // customer, discount, payment method, the expiry warning — is one CartState, and switching is
        // nothing more than pointing the screen at a different one and redrawing the grid. No query
        // runs, no control is rebuilt, and no stock moves: stock is allocated only when an invoice is
        // completed, exactly as before, so two carts holding the same drug are as safe as one.
        //
        // The carts live in a static store rather than on the module because navigating away from
        // the POS disposes the module; a pending invoice has to survive the cashier looking something
        // up on another screen. The store is cleared at logout.

        /// <summary>Everything that belongs to one customer's invoice-in-progress.</summary>
        private sealed class CartState
        {
            public int Number;                                    // the tab's number, stable for its life
            public readonly List<Row> Lines = new List<Row>();
            public Customer CreditCustomer;
            public decimal Discount;
            public int PaymentIndex;
            public string Warn = "";

            public decimal Subtotal => Lines.Sum(r => r.Total);
        }

        private static readonly List<CartState> Carts = new List<CartState>();
        private static CartState _active;

        /// <summary>Forgets every pending invoice — called at logout, so the next cashier starts clean.</summary>
        public static void ResetCarts() { Carts.Clear(); _active = null; }

        private static CartState EnsureActive()
        {
            if (_active == null)
            {
                if (Carts.Count == 0) Carts.Add(new CartState { Number = 1 });
                _active = Carts[0];
            }
            return _active;
        }

        // The rest of the module keeps reading and writing "the cart" and "the credit customer" as it
        // always did; both now resolve to the active state, so no line of the selling logic changed.
        private List<Row> _cart => EnsureActive().Lines;
        private Customer _creditCustomer
        {
            get => EnsureActive().CreditCustomer;
            set => EnsureActive().CreditCustomer = value;
        }

        private TextBox _search;
        private DataGridView _results, _cartGrid;
        private List<ItemStockView> _resultRows = new List<ItemStockView>();
        private Label _total, _warn, _customerLabel, _pendingLabel;
        private NumericUpDown _discount;
        private ComboBox _payment;
        private FlowLayoutPanel _tabs;
        private bool _loadingCart;   // true while the controls are being set FROM a state, not by the cashier

        // Payment options (V1.3): label shown to the cashier -> value stored on the sale.
        // (label, stored code) — from the one list every report and printout reads, so a method added
        // there reaches the receipt and the shift report too. The pair used to be typed here by hand,
        // and أوكاش went in backwards: the Arabic word became the stored code.
        private static readonly (string Label, string Value)[] PaymentOptions =
            PaymentMethods.All.Select(m => (m.LabelAr, m.Code)).ToArray();
        private Sale _lastSale;
        private readonly Timer _searchDebounce = new Timer { Interval = 250 };

        public PosModule()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            var title = new Label { Text = "نقطة البيع", Font = Theme.Title(18f), ForeColor = Theme.Primary, Dock = DockStyle.Top, Height = 36 };

            // ----- right side (search + results) -----
            var left = new Panel { Dock = DockStyle.Fill };
            _search = new TextBox { Dock = DockStyle.Top, Font = Theme.Base(15f), Height = 38 };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); ReloadResults(); };
            _search.TextChanged += (s, e) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
            _search.KeyDown += SearchKeyDown;
            var hint = new Label { Text = "امسح الباركود لإضافة الصنف مباشرة • F2 بحث • Enter لإضافة • F9 نقدي • F10 آجل • ✕ أو Del لحذف السطر • Ctrl+N فاتورة جديدة • Ctrl+1..9 تبديل", Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextMuted, Font = Theme.Base(9f) };

            _results = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true };
            Theme.StyleGrid(_results);
            _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصنف", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "سعر العلبة", DataPropertyName = "Box", Width = 100 });
            _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المتاح", DataPropertyName = "Avail", Width = 80 });
            _results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "أقرب صلاحية", DataPropertyName = "Expiry", Width = 110 });
            _results.KeyDown += ResultsKeyDown;
            _results.CellDoubleClick += (s, e) => AddSelectedResult();
            left.Controls.Add(_results);
            left.Controls.Add(hint);
            left.Controls.Add(_search);

            // ----- left side (cart + totals) -----
            var right = new Panel { Dock = DockStyle.Left, Width = 560 };
            // AllowUserToDeleteRows must stay off: the grid's own Del would drop the row out of the
            // grid while _cart kept the line, and the total would go on counting an item the cashier
            // can no longer see. Every removal goes through RemoveCartLine instead.
            _cartGrid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, AllowUserToDeleteRows = false };
            Theme.StyleGrid(_cartGrid);
            _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصنف", Name = "name", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            var unitCol = new DataGridViewComboBoxColumn { HeaderText = "الوحدة", Name = "unit", Width = 90, FlatStyle = FlatStyle.Flat };
            unitCol.Items.AddRange(UnitLabels);
            _cartGrid.Columns.Add(unitCol);
            _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الكمية", Name = "qty", Width = 70 });
            _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "السعر", Name = "price", ReadOnly = true, Width = 90 });
            _cartGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الإجمالي", Name = "line", ReadOnly = true, Width = 100 });
            // ✕ on every line: the cashier removes a mistyped item with the mouse, without having to
            // select the row first and find Del (V1.8). Same effect as Del, one click.
            var removeCol = new DataGridViewButtonColumn
            {
                HeaderText = "", Name = "remove", Text = "✕", UseColumnTextForButtonValue = true,
                Width = 42, FlatStyle = FlatStyle.Flat, ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    ForeColor = Theme.Danger,
                    Font = Theme.Base(11f, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            };
            _cartGrid.Columns.Add(removeCol);
            _cartGrid.CellContentClick += CartCellContentClick;
            _cartGrid.CellMouseEnter += CartCellMouseEnter;
            _cartGrid.CellMouseLeave += (s, e) => _cartGrid.Cursor = Cursors.Default;
            _cartGrid.CellEndEdit += CartCellEndEdit;
            _cartGrid.CurrentCellDirtyStateChanged += (s, e) => { if (_cartGrid.IsCurrentCellDirty) _cartGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _cartGrid.KeyDown += CartKeyDown;
            _cartGrid.DataError += (s, e) => { e.ThrowException = false; };

            // Height is the sum of its bands: total 40 + warning 24 + customer 22 + discount row 34 on
            // top, tab strip 40 + buttons 50 at the bottom — 210, plus a little air. Short of that the
            // strip is clipped behind the discount row.
            var totalsPanel = new Panel { Dock = DockStyle.Bottom, Height = 218, BackColor = Theme.Surface };
            _warn = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Accent, Font = Theme.Base(10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };
            _customerLabel = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextMuted, Font = Theme.Base(10f), TextAlign = ContentAlignment.MiddleRight };

            var discRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.RightToLeft };
            discRow.Controls.Add(new Label { Text = "خصم:", AutoSize = true, Margin = new Padding(6, 8, 4, 0), Font = Theme.Base(11f) });
            _discount = new NumericUpDown { Width = 110, Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Font = Theme.Base(12f) };
            _discount.ValueChanged += (s, e) => { if (!_loadingCart) EnsureActive().Discount = _discount.Value; UpdateTotals(); };
            discRow.Controls.Add(_discount);
            discRow.Controls.Add(new Label { Text = "طريقة الدفع:", AutoSize = true, Margin = new Padding(14, 8, 4, 0), Font = Theme.Base(11f) });
            _payment = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Base(12f) };
            foreach (var p in PaymentOptions) _payment.Items.Add(p.Label);
            _payment.SelectedIndex = 0;   // كاش
            _payment.SelectedIndexChanged += (s, e) => { if (!_loadingCart) EnsureActive().PaymentIndex = Math.Max(0, _payment.SelectedIndex); };
            discRow.Controls.Add(_payment);

            _total = new Label { Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Primary, Font = Theme.Title(20f), TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 10, 0) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(Btn("نقدي (F9)", () => CompleteSale(SaleType.Cash), primary: true));
            buttons.Controls.Add(Btn("آجل (F10)", () => CompleteSale(SaleType.Credit)));
            buttons.Controls.Add(Btn("مسح", ClearCart));
            buttons.Controls.Add(Btn("طباعة آخر", ReprintLast));

            // The tab strip: one numbered button per pending invoice, the active one filled, and [+].
            // It sits between the discount/payment row and the action buttons — the cashier's hand is
            // already there when they finish one customer and turn to the next.
            var tabBar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Surface };
            _tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(4, 3, 4, 0), AutoScroll = true };
            _pendingLabel = new Label { Dock = DockStyle.Left, Width = 110, Font = Theme.Base(9.5f), ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            tabBar.Controls.Add(_tabs);
            tabBar.Controls.Add(_pendingLabel);

            // Docking resolves last-added outermost: the Top band reads _total, warning, customer,
            // discount row from the top down; the Bottom band puts the buttons on the very edge and the
            // tab strip just above them.
            totalsPanel.Controls.Add(discRow);
            totalsPanel.Controls.Add(_customerLabel);
            totalsPanel.Controls.Add(_warn);
            totalsPanel.Controls.Add(_total);
            totalsPanel.Controls.Add(tabBar);
            totalsPanel.Controls.Add(buttons);

            right.Controls.Add(_cartGrid);
            right.Controls.Add(totalsPanel);

            Controls.Add(left);
            Controls.Add(right);
            Controls.Add(title);

            // Show whichever cart was active when the cashier last left this screen.
            LoadActiveIntoUi();
            RenderCart();
        }

        // ---------- carts ----------

        /// <summary>Copies what the controls hold into the active state, before pointing them elsewhere.</summary>
        private void SaveUiIntoActive()
        {
            CartState c = EnsureActive();
            c.Discount = _discount.Value;
            c.PaymentIndex = Math.Max(0, _payment.SelectedIndex);
            c.Warn = _warn.Text ?? "";
        }

        /// <summary>Points the controls at the active state. The grid is redrawn by the caller.</summary>
        private void LoadActiveIntoUi()
        {
            CartState c = EnsureActive();
            _loadingCart = true;
            try
            {
                _discount.Value = Math.Min(_discount.Maximum, Math.Max(_discount.Minimum, c.Discount));
                _payment.SelectedIndex = Math.Min(Math.Max(0, c.PaymentIndex), _payment.Items.Count - 1);
                _warn.Text = c.Warn ?? "";
            }
            finally { _loadingCart = false; }
        }

        /// <summary>Opens a new empty invoice and switches to it. Numbers are the lowest free one, so
        /// after [1][2][3] → complete 2 → [1][3], the next customer is [2] again, not [4].</summary>
        private void NewCart()
        {
            int number = 1;
            while (Carts.Any(c => c.Number == number)) number++;

            var cart = new CartState { Number = number };
            Carts.Add(cart);
            SwitchTo(cart);
        }

        private void SwitchTo(CartState cart)
        {
            if (cart == null || !Carts.Contains(cart)) return;
            if (!ReferenceEquals(cart, _active))
            {
                // A half-typed quantity belongs to the cart it was typed on; commit it there first.
                _cartGrid.EndEdit();
                SaveUiIntoActive();
                _active = cart;
                LoadActiveIntoUi();
            }
            RenderCart();
            _search.Focus();
        }

        private void SwitchToNumber(int number)
        {
            CartState cart = Carts.FirstOrDefault(c => c.Number == number);
            if (cart != null) SwitchTo(cart);
        }

        private void SwitchToNext()
        {
            if (Carts.Count < 2) return;
            int i = Carts.IndexOf(EnsureActive());
            SwitchTo(Carts[(i + 1) % Carts.Count]);
        }

        /// <summary>
        /// Drops the active invoice — completed or abandoned — and moves to a neighbour: the one to
        /// its right in the strip, else the one to its left, else a fresh empty one, so the screen
        /// never sits on a tab that no longer exists and never shows a customer's cart under another
        /// customer's number.
        /// </summary>
        private void CloseActiveCart()
        {
            CartState closing = EnsureActive();
            int i = Carts.IndexOf(closing);
            Carts.Remove(closing);

            CartState next;
            if (Carts.Count == 0) { next = new CartState { Number = 1 }; Carts.Add(next); }
            else next = Carts[Math.Min(i, Carts.Count - 1)];

            _active = next;
            LoadActiveIntoUi();
            RenderCart();
            _search.Focus();
        }

        /// <summary>
        /// Keeps the strip in step with the carts: a filled pill for the active invoice, outlined ones
        /// for the rest, each showing its number and how many lines it holds, and [+] at the end.
        ///
        /// Called on every cart change, so it must be cheap and must not dispose the button whose
        /// click it is running under. When the SET of carts is unchanged the existing buttons are
        /// updated in place; when a cart was opened or closed the strip is rebuilt — but deferred to
        /// after the current event, so a tab is never destroyed inside its own Click.
        /// </summary>
        private readonly Dictionary<CartState, PillButton> _tabButtons = new Dictionary<CartState, PillButton>();
        private bool _tabRebuildQueued;

        private void RefreshTabs()
        {
            if (_tabs == null) return;

            bool sameSet = _tabButtons.Count == Carts.Count && Carts.All(_tabButtons.ContainsKey);
            if (sameSet)
            {
                CartState active = EnsureActive();
                foreach (var kv in _tabButtons) StyleTab(kv.Value, kv.Key, ReferenceEquals(kv.Key, active));
                UpdatePendingLabel();
                return;
            }

            if (_tabRebuildQueued) return;
            _tabRebuildQueued = true;
            if (IsHandleCreated) BeginInvoke((Action)RebuildTabs);
            else RebuildTabs();
        }

        private void RebuildTabs()
        {
            _tabRebuildQueued = false;
            if (_tabs == null || _tabs.IsDisposed) return;

            _tabs.SuspendLayout();
            try
            {
                foreach (Control c in _tabs.Controls) c.Dispose();
                _tabs.Controls.Clear();
                _tabButtons.Clear();

                CartState active = EnsureActive();
                foreach (CartState cart in Carts.OrderBy(c => c.Number))
                {
                    CartState captured = cart;
                    var b = new PillButton { Height = 32, CornerRadius = 8, Margin = new Padding(3, 0, 3, 0) };
                    StyleTab(b, cart, ReferenceEquals(cart, active));
                    b.Click += (s, e) => SwitchTo(captured);
                    _tabs.Controls.Add(b);
                    _tabButtons[cart] = b;
                }

                var add = new PillButton { Text = "+", Width = 40, Height = 32, Outline = true, CornerRadius = 8, Margin = new Padding(8, 0, 3, 0), Font = Theme.Base(12f, FontStyle.Bold) };
                add.Click += (s, e) => NewCart();
                _tabs.Controls.Add(add);

                UpdatePendingLabel();
            }
            finally { _tabs.ResumeLayout(); }
        }

        private static void StyleTab(PillButton b, CartState cart, bool isActive)
        {
            int lines = cart.Lines.Count;
            string text = lines == 0 ? cart.Number.ToString() : cart.Number + "  (" + lines + ")";
            int width = lines == 0 ? 44 : 74;
            if (b.Text != text) b.Text = text;
            if (b.Width != width) b.Width = width;
            bool wantOutline = !isActive;   // Outline is the INACTIVE look
            if (b.Outline != wantOutline || b.Font.Bold != isActive)
            {
                b.Outline = wantOutline;
                b.Font = Theme.Base(10.5f, isActive ? FontStyle.Bold : FontStyle.Regular);
                b.Invalidate();
            }
        }

        private void UpdatePendingLabel()
        {
            int pending = Carts.Count(c => c.Lines.Count > 0);
            _pendingLabel.Text = pending == 0 ? "" : "معلّقة: " + pending;
        }

        public override void OnActivated() { ReloadResults(); _search.Focus(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F2: _search.Focus(); _search.SelectAll(); return true;
                case Keys.F9: CompleteSale(SaleType.Cash); return true;
                case Keys.F10: CompleteSale(SaleType.Credit); return true;
                // Del is caught here, not on the grid, so it removes the highlighted line wherever the
                // cashier is standing — after a scan the focus is in the search box, and the old
                // grid-only handler simply never ran (the line stayed, and so did its total).
                case Keys.Delete: if (TryRemoveCurrentCartLine()) return true; break;

                // Several customers at once: a new invoice, the next one, or one by its number.
                case Keys.Control | Keys.N: NewCart(); return true;
                case Keys.Control | Keys.Tab: SwitchToNext(); return true;
            }
            if ((keyData & Keys.Control) == Keys.Control)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D9) { SwitchToNumber(key - Keys.D0); return true; }
                if (key >= Keys.NumPad1 && key <= Keys.NumPad9) { SwitchToNumber(key - Keys.NumPad0); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- search / results ----------

        private void ReloadResults()
        {
            try
            {
                // Only priced items are sellable — unpriced ones (NULL price) never show in the POS.
                _resultRows = Session.Services.Inventory.SearchSellable(_search.Text, 40).ToList();
                _results.DataSource = _resultRows.Select(v => new
                {
                    v.Item.Id,
                    Name = v.Item.DisplayName,
                    Box = Fmt.Money(UnitConverter.PriceOf(v.Item, UnitType.Box)),
                    Avail = v.AvailableUnits,
                    Expiry = Fmt.Date(v.NearestExpiry)
                }).ToList();
            }
            catch (Exception ex) { Log.Error("POS reload", ex); Msg.Error(ex.Message); }
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // FR-QRC-07: a scanned code adds the item directly (qty 1; rescans increment).
                if (!TryScanCode(_search.Text)) AddTypedSearch();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _results.Rows.Count > 0) { _results.Focus(); _results.CurrentCell = _results.Rows[0].Cells[0]; }
        }

        /// <summary>
        /// Enter in the search box adds the best match for what is standing in the box right now.
        /// The results list is rebuilt on a 250 ms debounce, and a cashier types a name and hits Enter
        /// well inside that — so the grid could still be showing the previous search, which at the start
        /// of a sale is the whole catalogue. Adding "the selected row" then meant adding row one of that
        /// stale list: a name matching nothing quietly put the first item of the products table in the
        /// cart. So the pending search is run first, and a term with no match says so instead of selling
        /// something the cashier never asked for.
        /// </summary>
        private void AddTypedSearch()
        {
            if (_search.TextLength == 0) return;   // Enter on an empty box is not a request to add anything

            if (_searchDebounce.Enabled) { _searchDebounce.Stop(); ReloadResults(); }

            if (_resultRows.Count == 0)
            {
                Msg.Warn($"لا يوجد صنف مطابق لـ \"{_search.Text.Trim()}\".");
                _search.SelectAll();
                _search.Focus();
                return;
            }
            AddSelectedResult();
        }

        private bool TryScanCode(string term)
        {
            term = (term ?? "").Trim();
            if (term.Length == 0) return false;
            Item item = Session.Services.Codes.ResolveItem(term);
            if (item == null) return false;
            if (!item.SellingPrice.HasValue)
            {
                Msg.Warn($"الصنف \"{item.NameEn}\" بدون سعر بيع — استلم مخزوناً له أولاً ليُحسب سعره تلقائياً.");
                _search.Clear();
                return true;   // the code matched; don't fall through to name search
            }
            AddItem(item);
            _search.Clear();
            _search.Focus();
            return true;
        }

        private void ResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { AddSelectedResult(); e.Handled = e.SuppressKeyPress = true; }
        }

        private void AddSelectedResult()
        {
            // The Id is not a displayed column; map the selected row back by index into _resultRows.
            // Falling back to the first row is only a safety net for a grid holding rows without a
            // current one — the rows here always belong to the term that was searched for, because the
            // one caller that can arrive with a search still pending (AddTypedSearch) runs it first.
            ItemStockView v = null;
            int idx = _results.CurrentRow?.Index ?? -1;
            if (idx >= 0 && idx < _resultRows.Count) v = _resultRows[idx];
            if (v == null) v = _resultRows.FirstOrDefault();
            if (v == null) return;
            AddItem(v.Item);
            _search.Clear();
            _search.Focus();
        }

        // ---------- cart ----------

        private void AddItem(Item item)
        {
            Row row = _cart.FirstOrDefault(r => r.Item.Id == item.Id);
            if (row != null) row.Qty += 1;
            else { row = new Row { Item = item, Unit = UnitType.Box, Qty = 1 }; _cart.Add(row); }

            DateTime? warn = Session.Services.Pos.NearExpiryWarning(item.Id);
            _warn.Text = warn.HasValue ? $"تنبيه: {item.NameEn} أقرب صلاحية {Fmt.Date(warn)}" : "";
            EnsureActive().Warn = _warn.Text;
            ShowSubstituteInfo(item);

            RenderCart();
            // focus the qty cell of this row for the "search → Enter → qty → Enter" flow
            int idx = _cart.IndexOf(row);
            if (idx >= 0)
            {
                _cartGrid.CurrentCell = _cartGrid.Rows[idx].Cells["qty"];
            }
        }

        /// <summary>V1.2 req 4: if the drug is marked as a substitute, pop up what it substitutes.</summary>
        private void ShowSubstituteInfo(Item item)
        {
            if (!item.SubstituteOf.HasValue) return;
            try
            {
                Item original = Session.Services.Items.GetById(item.SubstituteOf.Value);
                if (original != null)
                    Msg.Info($"\"{item.NameEn}\" بديل للدواء: {original.NameEn}", "دواء بديل");
            }
            catch { /* informational only — never block the sale */ }
        }

        private void RenderCart()
        {
            _cartGrid.Rows.Clear();
            foreach (Row r in _cart)
            {
                int i = _cartGrid.Rows.Add();
                var cells = _cartGrid.Rows[i].Cells;
                cells["name"].Value = r.Item.DisplayName;
                cells["unit"].Value = UnitConverter.LabelAr(r.Unit);
                cells["qty"].Value = r.Qty;
                cells["price"].Value = Fmt.Money(r.UnitPrice);
                cells["line"].Value = Fmt.Money(r.Total);
            }
            UpdateTotals();
            RefreshTabs();
        }

        private void CartCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _cart.Count) return;
            Row r = _cart[e.RowIndex];
            var cells = _cartGrid.Rows[e.RowIndex].Cells;

            if (_cartGrid.Columns[e.ColumnIndex].Name == "qty")
            {
                if (int.TryParse(Convert.ToString(cells["qty"].Value), out int q) && q > 0) r.Qty = q;
            }
            else if (_cartGrid.Columns[e.ColumnIndex].Name == "unit")
            {
                r.Unit = LabelToUnit(Convert.ToString(cells["unit"].Value));
            }
            cells["qty"].Value = r.Qty;
            cells["unit"].Value = UnitConverter.LabelAr(r.Unit);
            cells["price"].Value = Fmt.Money(r.UnitPrice);
            cells["line"].Value = Fmt.Money(r.Total);
            UpdateTotals();
        }

        /// <summary>The ✕ button on a cart line removes that line — the mouse half of <see cref="Keys.Delete"/>.</summary>
        private void CartCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_cartGrid.Columns[e.ColumnIndex].Name != "remove") return;
            PostRemoveCartLine(e.RowIndex);
        }

        /// <summary>Del removes the highlighted cart line, exactly as the ✕ button does — including while
        /// a quantity cell is open for typing, which is where the focus sits right after an item is added.
        /// Letting Del through in that state used to hand the key to the grid's own row delete, which drops
        /// the row but not the line behind it, leaving its money in the total. Backspace still edits the
        /// quantity. The one Del left alone is in a search box that has text: there it means a character.</summary>
        private bool TryRemoveCurrentCartLine()
        {
            bool inCart = _cartGrid.ContainsFocus;
            bool inEmptySearch = _search.Focused && _search.TextLength == 0;
            if (!inCart && !inEmptySearch) return false;

            int index = _cartGrid.CurrentRow?.Index ?? -1;
            if (index < 0 || index >= _cart.Count) return false;

            PostRemoveCartLine(index);
            return true;
        }

        /// <summary>Both routes into <see cref="RemoveCartLine"/> post the work instead of running it in
        /// place, so the cart is never rebuilt while the grid is still busy with the click or the keypress
        /// that asked for it. Key and button therefore run the same code in the same conditions.</summary>
        private void PostRemoveCartLine(int index)
        {
            BeginInvoke((Action)(() => RemoveCartLine(index)));
        }

        /// <summary>A hand cursor over the ✕ so it reads as a button, not a character in the row.</summary>
        private void CartCellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            bool overRemove = e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                              _cartGrid.Columns[e.ColumnIndex].Name == "remove";
            _cartGrid.Cursor = overRemove ? Cursors.Hand : Cursors.Default;
        }

        /// <summary>The one place a cart line is dropped — both Del and the ✕ button end up here, so the
        /// grid, the expiry warning and the total can never disagree about what is still in the cart.
        /// The highlight then moves to the line that took its place; an emptied cart hands the keyboard
        /// back to the search box.</summary>
        private void RemoveCartLine(int index)
        {
            if (index < 0 || index >= _cart.Count) return;

            // Shut the cell editor down first, while the grid and _cart still agree: CancelEdit throws
            // away a half-typed quantity on the row that is leaving, and EndEdit takes the editing
            // control off the row — RenderCart's Rows.Clear() would throw with it still up.
            _cartGrid.CancelEdit();
            _cartGrid.EndEdit();

            _cart.RemoveAt(index);
            if (_cart.Count == 0) { _warn.Text = ""; EnsureActive().Warn = ""; }

            RenderCart();                    // redraws the rows and recomputes the total

            if (_cart.Count == 0) { _search.Focus(); return; }
            int next = Math.Min(index, _cart.Count - 1);
            if (next < _cartGrid.Rows.Count) _cartGrid.CurrentCell = _cartGrid.Rows[next].Cells["qty"];
        }

        private void CartKeyDown(object sender, KeyEventArgs e)
        {
            // Del is handled in ProcessCmdKey (see TryRemoveCurrentCartLine) so that one routine serves
            // both the key and the ✕ button; it reaches this grid too, before this handler ever runs.
            if (e.KeyCode == Keys.Enter && _cartGrid.CurrentCell != null &&
                     _cartGrid.Columns[_cartGrid.CurrentCell.ColumnIndex].Name == "qty")
            {
                _search.Focus(); e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void UpdateTotals()
        {
            decimal subtotal = _cart.Sum(r => r.Total);
            decimal discount = _discount.Value;
            if (discount > subtotal) discount = subtotal;
            _total.Text = "الإجمالي: " + Fmt.Money(subtotal - discount);
            _customerLabel.Text = _creditCustomer == null ? "" : "عميل الآجل: " + _creditCustomer.Name;
        }

        /// <summary>
        /// Abandons the active invoice. With other invoices pending its tab closes and the screen moves
        /// to a neighbour; as the only one it is simply emptied, as it always was. An invoice with lines
        /// on it asks first — one click must not throw away a customer's whole order.
        /// </summary>
        private void ClearCart()
        {
            if (_cart.Count > 0 && !Msg.Confirm("إلغاء هذه الفاتورة وحذف أصنافها؟")) return;

            if (Carts.Count > 1) { CloseActiveCart(); return; }

            _cart.Clear();
            _creditCustomer = null;
            _discount.Value = 0;
            _warn.Text = "";
            EnsureActive().Warn = "";
            RenderCart();
            _search.Focus();
        }

        // ---------- complete ----------

        private void CompleteSale(SaleType type)
        {
            if (_cart.Count == 0) { Msg.Info("لا توجد أصناف."); return; }

            int? customerId = null;
            if (type == SaleType.Credit)
            {
                if (_creditCustomer == null)
                {
                    using (var picker = new CustomerPickerForm())
                    {
                        if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
                        _creditCustomer = picker.Selected;
                    }
                }
                customerId = _creditCustomer?.Id;
            }

            var lines = _cart.Select(r => new CartLine { ItemId = r.Item.Id, UnitType = r.Unit, Quantity = r.Qty }).ToList();
            string paymentMethod = type == SaleType.Credit ? null : PaymentOptions[Math.Max(0, _payment.SelectedIndex)].Value;
            try
            {
                Sale sale = Session.Services.Pos.Complete(Session.CurrentUser, lines, type, customerId, _discount.Value, AppConfig.TerminalName, paymentMethod);
                _lastSale = sale;
                // The invoice is saved: its tab goes, before printing or any dialog can pump a stray
                // click back into a cart that no longer exists.
                CloseActiveCart();
                PrintReceipt(sale, ask: false);
                Msg.Info($"تمت الفاتورة رقم {sale.SaleNumber}\nالإجمالي: {Fmt.Money(sale.Total)}");
                ReloadResults();
            }
            catch (InsufficientStockException ex)
            {
                Msg.Warn(ex.Message + "\nيرجى تحديث الكميات وإعادة المحاولة.");
                ReloadResults();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error("تعذّر إتمام البيع: " + ex.Message); }
        }

        private void PrintReceipt(Sale sale, bool ask)
        {
            try
            {
                var info = Session.Services.CreateReceiptInfo(Session.CurrentUser.FullName ?? Session.CurrentUser.Username);
                // Role decides the format on the system's default printer (V1.3): the manager gets a full
                // A4 invoice; the cashier/employee gets a narrow 80mm receipt. Reprint (ask) offers a picker.
                if (Session.IsAdmin)
                {
                    InvoicePrinter.Print(FindForm(), sale, info, showDialog: ask);
                    return;
                }

                if (ask)
                {
                    // A reprint is never urgent, so it is checked on screen first and printed from
                    // there, rather than finding out what came off the roll after the paper is spent.
                    Dawaii.App.Printing.ThermalReceipt.ShowPreview(FindForm(), sale, info);
                    return;
                }

                // The counter receipt goes to the head as a rasterised image (V2.2). Only if that
                // cannot be done — no printer, a PDF writer, a spooler that refuses — does it fall
                // back to drawing a page through the driver, which is what used to lose the labels.
                string reason;
                string configured = Session.Services.ReceiptPrinterName;
                if (Dawaii.App.Printing.ThermalReceipt.TryPrint(sale, info, configured, out reason)) return;

                // A virtual printer returns false with no reason — that is the intended fallback. A real
                // fault returns false WITH one, and used to be treated the same way: the receipt silently
                // went to the driver path, and the cashier was never told the thermal head had refused.
                // The fault is logged and the fallback is still attempted, so a receipt still comes out
                // when it can — but the cashier now knows to look at the printer.
                if (!string.IsNullOrEmpty(reason))
                {
                    Log.Error("Thermal receipt", new Exception(reason));
                    Msg.Warn("تعذّرت الطباعة على طابعة الإيصالات:\n" + reason +
                             "\n\nسيتم محاولة الطباعة عبر تعريف الطابعة. تحقق من الطابعة.");
                }
                ReceiptDocumentPrinter.Print(FindForm(), sale, info, showDialog: false);
            }
            catch (Exception ex)
            {
                // The sale is already committed; a printer that is off or unplugged must not look like a
                // failed sale. Say so, and say what to do.
                Log.Error("Receipt print", ex);
                Msg.Warn("تعذّرت الطباعة: " + ex.Message +
                         "\n\nالفاتورة محفوظة. يمكن إعادة طباعتها من زر \"إعادة طباعة\" بعد فحص الطابعة.");
            }
        }

        private void ReprintLast()
        {
            if (_lastSale == null) { Msg.Info("لا توجد فاتورة سابقة."); return; }
            PrintReceipt(_lastSale, ask: true);
        }

        /// <summary>The cart's unit drop-down back to a <see cref="UnitType"/>. Anything unrecognised
        /// falls back to the single tablet, which is the smallest quantity and so can never oversell.</summary>
        private static UnitType LabelToUnit(string label)
        {
            if (label == "علبة") return UnitType.Box;
            if (label == "شريط") return UnitType.Strip;
            return UnitType.Unit;
        }

        private static Button Btn(string text, Action onClick, bool primary = false)
            => Theme.ActionButton(text, onClick, primary, width: 128);
    }
}
