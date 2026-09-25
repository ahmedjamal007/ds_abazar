using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Printing;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// Reprinting an invoice already on file (V2.4).
    ///
    /// A customer loses the receipt, a printer jams halfway, the roll runs out mid-sale. Until now the
    /// only reprint was of whatever sale this session happened to have rung up last, which was no help
    /// at all to the customer who comes back on Tuesday about Saturday's invoice. So: type the number
    /// off the receipt, look at what comes up, and print it again.
    ///
    /// Open to everyone. There is nothing here to protect — the screen only reads, and a receipt is
    /// something the customer is entitled to a copy of anyway. Looking up a sale is already what the
    /// returns screen does for every cashier.
    ///
    /// NOTHING here writes. No invoice is created or altered, no stock moves, no total shifts, no
    /// balance changes. The sale is read from the database, drawn on screen, and — after the user
    /// confirms — drawn on paper. An invoice can be reprinted as many times as needed; the program
    /// keeps no count, because refusing the fourth copy would only punish a customer for the
    /// pharmacy's printer.
    ///
    /// The paper is produced by <see cref="ReceiptOutput"/>, the same code that prints a sale as it is
    /// completed. That is deliberate: "the reprint looks exactly like the original" is a promise worth
    /// something only if there is one printing path rather than two that can drift.
    /// </summary>
    public class ReprintModule : ModuleControl
    {
        private TextBox _number;
        private DataGridView _grid;
        private Label _summary, _details, _hint;
        private PillButton _print;
        private Sale _sale;

        public ReprintModule()
        {
            var title = new Label
            {
                Text = "إعادة طباعة فاتورة", Font = Theme.Title(18f), ForeColor = Theme.Primary,
                Dock = DockStyle.Top, Height = 36
            };

            _hint = new Label
            {
                Text = "اكتب رقم الفاتورة المطبوع على الإيصال ثم اضغط Enter • F2 للعودة إلى خانة الرقم • " +
                       "لا يتم إنشاء أي فاتورة جديدة ولا تتغيّر الكميات أو الحسابات",
                Dock = DockStyle.Top, Height = 24, ForeColor = Theme.TextMuted, Font = Theme.Base(9f)
            };

            // ----- the search row -----
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 56, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
            };
            top.Controls.Add(new Label
            {
                Text = "رقم الفاتورة:", AutoSize = true, Margin = new Padding(6, 16, 4, 0), Font = Theme.Base(11f)
            });
            _number = new TextBox { Width = 180, Font = Theme.Base(14f), Margin = new Padding(4, 11, 4, 0) };
            _number.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;      // no ding from the text box
                Find();
            };
            top.Controls.Add(_number);
            top.Controls.Add(Theme.ActionButton("بحث", Find, width: 110));
            top.Controls.Add(Theme.ActionButton("مسح", Clear, width: 100));

            // ----- what was found -----
            _summary = new Label
            {
                Dock = DockStyle.Top, Height = 34, Font = Theme.Base(13f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary, TextAlign = ContentAlignment.MiddleRight
            };
            _details = new Label
            {
                Dock = DockStyle.Top, Height = 28, Font = Theme.Base(10.5f),
                ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleRight
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            Theme.StyleGrid(_grid);
            Col("الصنف", fill: true);
            Col("الوحدة", 100);
            Col("الكمية", 90);
            Col("سعر الوحدة", 130);
            Col("الإجمالي", 140);

            // ----- printing -----
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 0, 0)
            };
            _print = (PillButton)Theme.ActionButton("إعادة طباعة الفاتورة", Reprint, primary: true, width: 210);
            _print.Enabled = false;             // nothing found yet, nothing to print
            bar.Controls.Add(_print);
            bar.Controls.Add(Theme.ActionButton("فاتورة أخرى", Clear, width: 140));

            Controls.Add(_grid);
            Controls.Add(bar);
            Controls.Add(_details);
            Controls.Add(_summary);
            Controls.Add(top);
            Controls.Add(_hint);
            Controls.Add(title);

            Clear();
        }

        public override void OnActivated() => _number.Focus();

        /// <summary>F2 puts the cursor back in the number box, as it does on the selling screen.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F2) { _number.Focus(); _number.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- finding ----------------

        /// <summary>How a search ended. Separated from the telling so the rule can be exercised
        /// without a message box in the way — the box is the part that cannot be tested.</summary>
        private enum Found { NothingTyped, NoSuchInvoice, Shown, Failed }

        /// <summary>Looks the invoice up and draws it. Says nothing to the user.</summary>
        private Found Lookup()
        {
            string query = (_number.Text ?? "").Trim();
            if (query.Length == 0) return Found.NothingTyped;

            try
            {
                // The same lookup the returns screen uses, so a receipt printed before V1.8 — whose
                // number is a composite rather than a plain integer — is still found by what is on it.
                Sale found = Session.Services.Pos.FindSale(query);
                Show(found);
                return found == null ? Found.NoSuchInvoice : Found.Shown;
            }
            catch (Exception ex)
            {
                Log.Error("Reprint lookup", ex);
                Show(null);
                _error = ex.Message;
                return Found.Failed;
            }
        }

        private string _error;

        private void Find()
        {
            switch (Lookup())
            {
                case Found.Shown:
                    return;

                case Found.NothingTyped:
                    Msg.Warn("أدخل رقم الفاتورة المطبوع على الإيصال.");
                    _number.Focus();
                    return;

                case Found.NoSuchInvoice:
                    Msg.Info("لا توجد فاتورة بهذا الرقم.");
                    _number.Focus();
                    _number.SelectAll();
                    return;

                default:
                    Msg.Error("تعذّر البحث عن الفاتورة: " + _error);
                    return;
            }
        }

        private void Clear()
        {
            _number.Text = "";
            Show(null);
            _number.Focus();
        }

        /// <summary>Draws the invoice exactly as it is on file, or empties the screen when given null.</summary>
        private void Show(Sale sale)
        {
            _sale = sale;
            _grid.Rows.Clear();

            if (sale == null)
            {
                _summary.Text = "";
                _details.Text = "";
                _print.Enabled = false;
                _print.Invalidate();
                return;
            }

            foreach (SaleLine line in sale.Lines)
            {
                _grid.Rows.Add(
                    line.ItemName ?? ("#" + line.ItemId),
                    UnitConverter.LabelAr(line.UnitType),
                    line.Quantity.ToString(),
                    // The price per sold unit — a box price for a box, not the per-tablet price it is
                    // stored as — which is the figure printed on the receipt the customer is holding.
                    Fmt.Money(line.UnitPrice * line.UnitsEach),
                    Fmt.Money(line.LineTotal));
            }

            string status = sale.Status == SaleStatus.Returned ? " — مُرجعة بالكامل"
                          : sale.ReturnedTotal > 0 ? " — بها إرجاع جزئي"
                          : "";

            _summary.Text = "فاتورة " + sale.SaleNumber + status +
                            "     الإجمالي: " + Fmt.Money(sale.Total) +
                            (sale.Discount > 0 ? "     الخصم: " + Fmt.Money(sale.Discount) : "");

            _details.Text = "التاريخ: " + Fmt.DateTime(sale.CreatedAt) +
                            "     الدفع: " + (sale.SaleType == SaleType.Credit
                                ? "آجل" : PaymentMethods.LabelAr(sale.PaymentMethod)) +
                            "     الأصناف: " + sale.Lines.Count;

            _print.Enabled = true;
            _print.Invalidate();
        }

        // ---------------- printing ----------------

        private void Reprint()
        {
            if (_sale == null) { Msg.Info("ابحث عن فاتورة أولاً."); return; }

            // Asked every time. Paper and a thermal roll are the pharmacy's to spend, and a reprint
            // fired by a stray Enter on a busy counter is exactly how a roll gets wasted.
            if (!Msg.Confirm("إعادة طباعة الفاتورة " + _sale.SaleNumber + "؟\n" +
                             "الإجمالي: " + Fmt.Money(_sale.Total) + " — " + _sale.Lines.Count + " صنف\n\n" +
                             "لن تُنشأ فاتورة جديدة ولن تتغيّر الكميات أو الحسابات."))
                return;

            // ask: false — straight to the configured receipt printer, the same way the original came
            // off it when the sale was rung up.
            ReceiptOutput.Print(FindForm(), _sale, ask: false);
        }

        // ---------------- layout helpers ----------------

        private void Col(string header, int width = 0, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = true };
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = width;
            _grid.Columns.Add(c);
        }
    }
}
