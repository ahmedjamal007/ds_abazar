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
    /// <summary>Logs an employee expense: money taken or medicine dispensed (V1.2 req 5).
    /// The medicine picker is a live search box (V1.3): type a name or scan/enter a barcode to find the item.</summary>
    public class ExpenseForm : BaseForm
    {
        private readonly int _userId;
        private RadioButton _money, _medicine;
        private NumericUpDown _amount, _units;
        private TextBox _itemSearch, _note;
        private ListBox _matches;
        private Label _pickedLabel;
        private ItemStockView _selected;
        private List<ItemStockView> _matchRows = new List<ItemStockView>();
        private readonly Timer _debounce = new Timer { Interval = 200 };

        public ExpenseForm(int userId, string userName)
        {
            _userId = userId;
            Text = "تسجيل مصروف — " + userName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(430, 470);

            _money = new RadioButton { Text = "مبلغ نقدي", Checked = true, Location = new Point(20, 16), AutoSize = true, Font = Theme.Base(11f) };
            _medicine = new RadioButton { Text = "دواء / صنف", Location = new Point(160, 16), AutoSize = true, Font = Theme.Base(11f) };
            _money.CheckedChanged += (s, e) => UpdateEnabled();

            AddLabel("المبلغ", 52);
            _amount = new NumericUpDown { Location = new Point(20, 74), Size = new Size(390, 28), Minimum = 0, Maximum = 100000000, DecimalPlaces = 2, Font = Theme.Base(12f) };

            AddLabel("ابحث بالاسم أو امسح الباركود", 112);
            _itemSearch = new TextBox { Location = new Point(20, 134), Size = new Size(390, 28), Font = Theme.Base(12f) };
            _itemSearch.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _itemSearch.KeyDown += SearchKeyDown;
            _debounce.Tick += (s, e) => { _debounce.Stop(); RefreshMatches(); };

            _matches = new ListBox { Location = new Point(20, 166), Size = new Size(390, 110), Font = Theme.Base(11f) };
            _matches.SelectedIndexChanged += (s, e) =>
            {
                if (_matches.SelectedIndex >= 0 && _matches.SelectedIndex < _matchRows.Count)
                    SetSelected(_matchRows[_matches.SelectedIndex]);
            };

            _pickedLabel = new Label { Location = new Point(20, 280), Size = new Size(390, 22), Font = Theme.Base(10.5f, FontStyle.Bold), ForeColor = Theme.Primary };

            AddLabel("الكمية (حبات)", 308);
            _units = new NumericUpDown { Location = new Point(20, 330), Size = new Size(390, 28), Minimum = 1, Maximum = 100000, Font = Theme.Base(12f) };

            AddLabel("ملاحظة", 366);
            _note = new TextBox { Location = new Point(20, 388), Size = new Size(390, 28), Font = Theme.Base(11f) };

            var ok = Theme.ActionButton("حفظ", Save, primary: true, width: 190);
            ok.Location = new Point(20, 424);
            var cancel = Theme.ActionButton("إلغاء", Close, width: 190);
            cancel.Location = new Point(220, 424);
            Controls.AddRange(new Control[] { _money, _medicine, _amount, _itemSearch, _matches, _pickedLabel, _units, _note, ok, cancel });
            UpdateEnabled();
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            string term = _itemSearch.Text.Trim();
            if (term.Length == 0) return;

            // A scanned/typed barcode resolves straight to its item; otherwise take the first search match.
            Item byCode = Session.Services.Codes.ResolveItem(term);
            if (byCode != null)
            {
                SetSelected(Session.Services.Inventory.GetView(byCode.Id));
                _itemSearch.Clear(); _matches.Items.Clear(); _matchRows.Clear();
                return;
            }
            RefreshMatches();
            if (_matchRows.Count > 0) SetSelected(_matchRows[0]);
        }

        private void RefreshMatches()
        {
            if (!_medicine.Checked) return;
            string term = _itemSearch.Text.Trim();
            try
            {
                // Stock views, not bare items: the employee needs to see what is on the shelf before
                // asking for a quantity the pharmacy cannot give.
                _matchRows = Session.Services.Inventory.Search(term, limit: 30).ToList();
            }
            catch { _matchRows = new List<ItemStockView>(); }

            _matches.BeginUpdate();
            _matches.Items.Clear();
            foreach (ItemStockView v in _matchRows)
            {
                string price = v.Item.SellingPrice.HasValue
                    ? $"{Fmt.Money(v.Item.SellingPrice.Value)}/حبة"
                    : "بدون سعر";
                string stock = v.AvailableUnits > 0 ? $"متوفر {v.AvailableUnits} حبة" : "غير متوفر";
                _matches.Items.Add($"{v.Item.DisplayName} ({price}) — {stock}");
            }
            _matches.EndUpdate();
        }

        private void SetSelected(ItemStockView view)
        {
            _selected = view;
            if (view == null) { _pickedLabel.Text = ""; return; }

            _pickedLabel.Text = view.AvailableUnits > 0
                ? $"المحدد: {view.Item.DisplayName} — المتوفر {view.AvailableUnits} حبة"
                : $"المحدد: {view.Item.DisplayName} — غير متوفر في المخزون";
            _pickedLabel.ForeColor = view.AvailableUnits > 0 ? Theme.Primary : Theme.Danger;

            // Keep the spinner inside what the shelf holds; the save check still guards the real limit
            // in case another till sells the same stock while this dialog is open.
            _units.Maximum = Math.Max(1, view.AvailableUnits);
            if (_units.Value > _units.Maximum) _units.Value = _units.Maximum;
        }

        private void UpdateEnabled()
        {
            bool money = _money.Checked;
            _amount.Enabled = money;
            _itemSearch.Enabled = _matches.Enabled = _units.Enabled = !money;
            if (!money && _matches.Items.Count == 0) RefreshMatches();
        }

        private void Save()
        {
            try
            {
                if (_money.Checked)
                {
                    Session.Services.Employees.AddMoneyExpense(Session.CurrentUser, _userId, _amount.Value, _note.Text.Trim());
                }
                else
                {
                    if (_selected == null) { Msg.Warn("ابحث عن صنف واختره."); return; }
                    Session.Services.Employees.AddMedicineExpense(Session.CurrentUser, _userId,
                        _selected.Item.Id, (int)_units.Value, _note.Text.Trim());
                }
                Msg.Info("تم تسجيل المصروف.");
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void AddLabel(string text, int y)
            => Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(20, y), Font = Theme.Base(10f), ForeColor = Theme.TextMuted });
    }
}
