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
    /// <summary>
    /// Look up an invoice by its number and give items back (FR-POS-08). Returns are per item and per
    /// quantity: a customer who bought 50 boxes of a medicine and brings 5 back gets those 5 refunded
    /// and back into stock, and the rest of the invoice stands.
    /// </summary>
    public class ReturnSaleForm : BaseForm
    {
        private const int QuantityColumn = 5;

        private TextBox _saleId;
        private DataGridView _grid;
        private Label _info;
        private Label _selection;
        private Sale _sale;

        public ReturnSaleForm()
        {
            Text = "إرجاع أصناف من فاتورة";
            ClientSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterParent;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, FlowDirection = FlowDirection.RightToLeft };
            top.Controls.Add(new Label { Text = "رقم الفاتورة:", AutoSize = true, Margin = new Padding(6, 14, 4, 0), Font = Theme.Base(11f) });
            _saleId = new TextBox { Width = 160, Font = Theme.Base(13f), Margin = new Padding(4, 10, 4, 0) };
            _saleId.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadSale(); } };
            top.Controls.Add(_saleId);
            top.Controls.Add(Btn("بحث", LoadSale));
            top.Controls.Add(Btn("إرجاع كل المتبقي", SelectEverything));
            top.Controls.Add(Btn("مسح الكميات", ClearQuantities));

            _info = new Label { Dock = DockStyle.Top, Height = 30, Font = Theme.Base(11f, FontStyle.Bold), ForeColor = Theme.TextPrimary };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(ReadOnlyCol("الصنف", DataGridViewAutoSizeColumnMode.Fill));
            _grid.Columns.Add(ReadOnlyCol("الوحدة", width: 90));
            _grid.Columns.Add(ReadOnlyCol("الكمية المباعة", width: 110));
            _grid.Columns.Add(ReadOnlyCol("سبق إرجاعه", width: 110));
            _grid.Columns.Add(ReadOnlyCol("المتبقي", width: 90));

            var qty = new DataGridViewTextBoxColumn
            {
                HeaderText = "كمية الإرجاع",
                Width = 120,
                ValueType = typeof(int),
                DefaultCellStyle = { BackColor = Theme.InputBg, Font = Theme.Base(11f, FontStyle.Bold) }
            };
            _grid.Columns.Add(qty);
            _grid.Columns.Add(ReadOnlyCol("قيمة الإرجاع", width: 130));

            _grid.CellValidating += ClampQuantity;
            _grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0) RefreshSelectionTotal(); };
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            // Double-clicking a row is the shortcut for "all of this one".
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) FillRowWithRemaining(_grid.Rows[e.RowIndex]); };

            _selection = new Label
            {
                Dock = DockStyle.Bottom, Height = 30, Font = Theme.Base(11f, FontStyle.Bold),
                ForeColor = Theme.Primary, TextAlign = ContentAlignment.MiddleRight
            };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.Add(Btn("إرجاع الكميات المحددة", ReturnSelected, primary: true));
            bottom.Controls.Add(Btn("إرجاع الفاتورة كاملة", ReturnWholeInvoice));
            bottom.Controls.Add(Btn("إغلاق", () => Close()));

            Controls.Add(_grid);
            Controls.Add(_selection);
            Controls.Add(_info);
            Controls.Add(top);
            Controls.Add(bottom);
        }

        // ---------------- lookup ----------------

        private void LoadSale()
        {
            _grid.Rows.Clear();
            _info.Text = "";
            _selection.Text = "";
            _sale = null;

            string query = _saleId.Text.Trim();
            if (query.Length == 0) { Msg.Warn("أدخل رقم الفاتورة المطبوع على الإيصال."); return; }
            try
            {
                _sale = Session.Services.Pos.FindSale(query);
                if (_sale == null) { Msg.Info("لا توجد فاتورة بهذا الرقم."); return; }
                ShowSale();
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private void ShowSale()
        {
            _grid.Rows.Clear();
            foreach (SaleLine line in _sale.Lines)
            {
                int index = _grid.Rows.Add(
                    line.ItemName ?? ("#" + line.ItemId),
                    UnitConverter.LabelAr(line.UnitType),
                    line.Quantity,
                    line.ReturnedQuantity,
                    line.RemainingQuantity,
                    0,
                    Fmt.Money(line.LineTotal));

                DataGridViewRow row = _grid.Rows[index];
                row.Tag = line;
                if (line.RemainingQuantity == 0)
                {
                    // Nothing left to give back on this line — grey it out rather than hide it, so the
                    // invoice still reads as what was originally sold.
                    row.ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = Theme.TextMuted;
                }
            }

            string status =
                _sale.Status == SaleStatus.Returned ? "  —  مُرجعة بالكامل" :
                _sale.Status == SaleStatus.PartiallyReturned ? $"  —  مُرجع منها {Fmt.Money(_sale.ReturnedTotal)}" : "";
            _info.Text = $"فاتورة رقم {_sale.SaleNumber}  —  {(_sale.SaleType == SaleType.Credit ? "آجل" : "نقدي")}" +
                         $"  —  {Fmt.DateTime(_sale.CreatedAt)}  —  الإجمالي {Fmt.Money(_sale.Total)}{status}";
            RefreshSelectionTotal();
        }

        // ---------------- quantity entry ----------------

        /// <summary>Keeps a typed quantity within what the line still has outstanding.</summary>
        private void ClampQuantity(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (e.ColumnIndex != QuantityColumn || e.RowIndex < 0) return;
            var line = _grid.Rows[e.RowIndex].Tag as SaleLine;
            if (line == null) return;

            if (!int.TryParse(Convert.ToString(e.FormattedValue), out int wanted) || wanted < 0)
            {
                _grid.Rows[e.RowIndex].Cells[QuantityColumn].Value = 0;
                e.Cancel = true;
                return;
            }
            if (wanted > line.RemainingQuantity)
            {
                Msg.Warn($"المتبقي من \"{line.ItemName}\" هو {line.RemainingQuantity} فقط.");
                _grid.Rows[e.RowIndex].Cells[QuantityColumn].Value = line.RemainingQuantity;
                e.Cancel = true;
            }
        }

        private void SelectEverything()
        {
            if (!HaveSale()) return;
            foreach (DataGridViewRow row in _grid.Rows) FillRowWithRemaining(row);
        }

        private void ClearQuantities()
        {
            foreach (DataGridViewRow row in _grid.Rows) row.Cells[QuantityColumn].Value = 0;
            RefreshSelectionTotal();
        }

        private void FillRowWithRemaining(DataGridViewRow row)
        {
            var line = row.Tag as SaleLine;
            if (line == null) return;
            row.Cells[QuantityColumn].Value = line.RemainingQuantity;
            RefreshSelectionTotal();
        }

        /// <summary>Prices whatever is currently typed in, so the refund is visible before confirming.</summary>
        private void RefreshSelectionTotal()
        {
            if (_sale == null) { _selection.Text = ""; return; }

            IList<ReturnRequest> requests = CurrentRequests();
            if (requests.Count == 0)
            {
                _selection.Text = _sale.Status == SaleStatus.Returned
                    ? "هذه الفاتورة مُرجعة بالكامل."
                    : "حدد الكمية المراد إرجاعها من كل صنف.";
                return;
            }

            try
            {
                ReturnPlan plan = ReturnCalculator.Plan(_sale, requests);
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var line = row.Tag as SaleLine;
                    ReturnLine priced = line == null ? null : plan.Lines.FirstOrDefault(l => l.SaleLineId == line.Id);
                    row.Cells[6].Value = priced != null ? Fmt.Money(priced.Amount) : Fmt.Money(line?.LineTotal ?? 0m);
                }
                _selection.Text = $"إجمالي الإرجاع: {Fmt.Money(plan.Total)}" +
                                  (plan.CompletesSale ? "   (سيتم إرجاع الفاتورة بالكامل)" : "");
            }
            catch (DomainException ex) { _selection.Text = ex.Message; }
        }

        private IList<ReturnRequest> CurrentRequests()
        {
            var requests = new List<ReturnRequest>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var line = row.Tag as SaleLine;
                if (line == null) continue;
                int quantity = ToInt(row.Cells[QuantityColumn].Value);
                if (quantity > 0) requests.Add(new ReturnRequest(line.Id, quantity));
            }
            return requests;
        }

        // ---------------- committing ----------------

        private void ReturnSelected()
        {
            if (!HaveSale()) return;
            IList<ReturnRequest> requests = CurrentRequests();
            if (requests.Count == 0) { Msg.Warn("حدد الكمية المراد إرجاعها من كل صنف."); return; }

            ReturnPlan plan;
            try { plan = ReturnCalculator.Plan(_sale, requests); }
            catch (DomainException ex) { Msg.Error(ex.Message); return; }

            string summary = string.Join("\n", plan.Lines.Select(l => $"• {l.ItemName}: {l.Quantity}"));
            Commit($"إرجاع الأصناف التالية واستعادتها للمخزون؟\n\n{summary}\n\nالمبلغ المسترد: {Fmt.Money(plan.Total)}",
                (reason) => Session.Services.Pos.ReturnItems(Session.CurrentUser, _sale.Id, requests, reason));
        }

        private void ReturnWholeInvoice()
        {
            if (!HaveSale()) return;
            Commit($"إرجاع كل المتبقي من الفاتورة واستعادته للمخزون؟\n\nالمبلغ المسترد: {Fmt.Money(_sale.Total - _sale.ReturnedTotal)}",
                (reason) => Session.Services.Pos.ReturnSale(Session.CurrentUser, _sale.Id, reason));
        }

        private void Commit(string confirmation, Func<string, Return> apply)
        {
            if (!Msg.Confirm(confirmation)) return;
            string reason = Prompt.Show("سبب الإرجاع", "إرجاع");
            if (string.IsNullOrWhiteSpace(reason)) return;
            try
            {
                Return done = apply(reason);
                Msg.Info($"تم إرجاع {done.Lines.Sum(l => l.Quantity)} وحدة بمبلغ {Fmt.Money(done.Total)}." +
                         (done.CompletedTheSale ? "\nالفاتورة الآن مُرجعة بالكامل." : ""));
                LoadSale();   // re-read so the remaining quantities are the stored ones
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private bool HaveSale()
        {
            if (_sale == null) { Msg.Info("ابحث عن فاتورة أولاً."); return false; }
            if (_sale.Status == SaleStatus.Returned) { Msg.Info("الفاتورة مُرجعة بالفعل."); return false; }
            return true;
        }

        // ---------------- small helpers ----------------

        private static int ToInt(object value)
            => value != null && int.TryParse(Convert.ToString(value), out int n) ? n : 0;

        private static DataGridViewTextBoxColumn ReadOnlyCol(string header,
            DataGridViewAutoSizeColumnMode sizeMode = DataGridViewAutoSizeColumnMode.NotSet, int width = 100)
            => new DataGridViewTextBoxColumn { HeaderText = header, AutoSizeMode = sizeMode, Width = width, ReadOnly = true };

        private static Button Btn(string text, Action onClick, bool primary = false)
            => Theme.ActionButton(text, onClick, primary);
    }
}
