using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// Choose the item for a stock-entry flow without pre-selecting a grid row (V1.3): the user types a
    /// name or scans/enters a barcode to find it, or presses "صنف جديد" to create a brand-new item on
    /// the spot. Returns the chosen item in <see cref="Selected"/>.
    /// </summary>
    public class ItemPickerForm : BaseForm
    {
        private TextBox _search;
        private ListBox _matches;
        private List<Item> _rows = new List<Item>();
        private readonly Timer _debounce = new Timer { Interval = 200 };

        public Item Selected { get; private set; }

        /// <summary>
        /// True when this picker is being used inside a supplier's delivery (V2.4).
        ///
        /// It decides which right "صنف جديد" needs. A delivery routinely carries a drug the
        /// pharmacy has never stocked, so on that path opening one travels with the buying side; the
        /// catalogue screen's own picker still asks for the stockroom right.
        /// </summary>
        private readonly bool _forDelivery;

        public ItemPickerForm(bool forDelivery = false)
        {
            _forDelivery = forDelivery;

            Text = "اختيار الصنف";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(440, 360);
            BackColor = Theme.Background;

            var hint = new Label
            {
                Text = "ابحث بالاسم أو امسح الباركود، أو أنشئ صنفاً جديداً",
                AutoSize = false, Location = new Point(20, 14), Size = new Size(400, 22),
                Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleRight
            };
            _search = new TextBox { Location = new Point(20, 40), Size = new Size(400, 30), Font = Theme.Base(13f) };
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _search.KeyDown += SearchKeyDown;
            _debounce.Tick += (s, e) => { _debounce.Stop(); RefreshMatches(); };

            _matches = new ListBox { Location = new Point(20, 78), Size = new Size(400, 200), Font = Theme.Base(11.5f) };
            _matches.DoubleClick += (s, e) => PickFromList();

            var pick = Theme.ActionButton("اختيار", PickFromList, primary: true, width: 130);
            pick.Location = new Point(20, 292);
            var create = Theme.ActionButton("صنف جديد", CreateNew, width: 130);
            create.Location = new Point(160, 292);
            // Offered only to someone who may actually go through with it. The service refuses anyone
            // else regardless, but a button that always ends in a permission error is worse than no
            // button: mid-delivery it reads as the program being broken.
            create.Visible = MayCreate;
            var cancel = Theme.ActionButton("إلغاء", Close, width: 130);
            cancel.Location = new Point(300, 292);

            Controls.AddRange(new Control[] { hint, _search, _matches, pick, create, cancel });
            AcceptButton = null;
            RefreshMatches();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _search.Focus(); }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            string term = _search.Text.Trim();
            if (term.Length == 0) return;

            // A scanned/typed barcode resolves straight to its item; otherwise take the first match.
            Item byCode = Session.Services.Codes.ResolveItem(term);
            if (byCode != null) { Select(byCode); return; }
            RefreshMatches();
            if (_rows.Count > 0) Select(_rows[0]);
        }

        private void RefreshMatches()
        {
            try { _rows = Session.Services.Items.Search(_search.Text.Trim(), activeOnly: true, limit: 50).ToList(); }
            catch { _rows = new List<Item>(); }

            _matches.BeginUpdate();
            _matches.Items.Clear();
            foreach (Item it in _rows)
                _matches.Items.Add(it.DisplayName);
            _matches.EndUpdate();
            if (_matches.Items.Count > 0) _matches.SelectedIndex = 0;
        }

        private void PickFromList()
        {
            int idx = _matches.SelectedIndex;
            if (idx < 0 || idx >= _rows.Count) { Msg.Info("اختر صنفاً من القائمة."); return; }
            Select(_rows[idx]);
        }

        /// <summary>Whether this user may open a new drug from this particular picker.</summary>
        private bool MayCreate => _forDelivery ? Session.CanManagePurchasing : Session.CanManageInventory;

        private void CreateNew()
        {
            if (!MayCreate) { Msg.Info("إضافة صنف جديد غير متاحة لك."); return; }

            try
            {
                using (var dlg = new ItemForm { PrefillCode = _search.Text.Trim() })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    int id = _forDelivery
                        ? Session.Services.Inventory.CreateItemForDelivery(Session.CurrentUser, dlg.Result)
                        : Session.Services.Inventory.CreateItem(Session.CurrentUser, dlg.Result);
                    if (!string.IsNullOrWhiteSpace(dlg.EnteredCode))
                    {
                        try { Session.Services.Codes.SetCode(Session.CurrentUser, id, dlg.EnteredCode); }
                        catch (DomainException ex) { Msg.Warn("لم يُضف الرمز: " + ex.Message); }
                    }
                    Select(Session.Services.Items.GetById(id));
                }
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void Select(Item item)
        {
            if (item == null) return;
            Selected = item;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
