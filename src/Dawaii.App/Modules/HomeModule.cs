using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;

namespace Dawaii.App.Modules
{
    /// <summary>
    /// Dashboard (Figma design): welcome header + "new invoice" action, four KPI tiles
    /// (today's sales, near-expiry, low stock, pending debts) and the latest-sales card.
    /// Same data as before — near-expiry/low-stock/debts/backup alerts all remain (FR-EXP-02, FR-BAK-03).
    /// </summary>
    public class HomeModule : ModuleControl
    {
        private KpiCard _sales, _expiry, _low, _debts;
        private Label _welcome, _subtitle, _backupWarn;
        private SalesListCard _recent;

        public HomeModule()
        {
            Padding = new Padding(28, 20, 28, 20);
            BuildUi();
        }

        private void BuildUi()
        {
            // Header row: welcome (right) + new-invoice button (left)
            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Background };
            _welcome = new Label
            {
                Font = Theme.Title(21f), ForeColor = Theme.TextPrimary, BackColor = Theme.Background,
                AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Top, Height = 42
            };
            _subtitle = new Label
            {
                Text = "إليك نظرة سريعة على أداء الصيدلية لهذا اليوم. تأكد من مراجعة تنبيهات المخزن.",
                Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted, BackColor = Theme.Background,
                AutoSize = false, TextAlign = ContentAlignment.TopRight, Dock = DockStyle.Top, Height = 26
            };
            header.Controls.Add(_subtitle);
            header.Controls.Add(_welcome);

            if (!Session.IsAdmin)
            {
                var newSale = new PillButton
                {
                    Text = "＋  فاتورة جديدة", Size = new Size(160, 46), SurfaceColor = Theme.Background
                };
                newSale.Click += (s, e) => (FindForm() as MainForm)?.OpenPos();
                header.Controls.Add(newSale);
                header.Resize += (s, e) => newSale.Location = new Point(header.Width - 160, 18);
                newSale.BringToFront();
            }

            _backupWarn = new Label
            {
                Text = "", Font = Theme.Base(10f, FontStyle.Bold), ForeColor = Theme.Danger, BackColor = Theme.Background,
                AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Top, Height = 0
            };

            // KPI row
            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 168, BackColor = Theme.Background,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0)
            };
            _sales = Card("إجمالي مبيعات اليوم", "cash", Theme.Primary, filled: true);
            _expiry = Card("أصناف تنتهي قريباً", "calendar", Color.FromArgb(200, 124, 12));
            _low = Card("أصناف منخفضة المخزون", "cart", Color.FromArgb(186, 44, 44));
            _debts = Card("ديون معلقة", "receipt", Color.FromArgb(90, 100, 96));
            cards.Controls.AddRange(new Control[] { _sales, _expiry, _low, _debts });

            // Latest sales card
            _recent = new SalesListCard { Dock = DockStyle.Top, Height = 330 };

            Controls.Add(_recent);
            Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Background });
            Controls.Add(cards);
            Controls.Add(_backupWarn);
            Controls.Add(header);
        }

        private static KpiCard Card(string label, string icon, Color accent, bool filled = false)
            => new KpiCard { Label = label, IconName = icon, Accent = accent, Filled = filled, Margin = new Padding(0, 4, 0, 4) };

        public override void OnActivated()
        {
            User u = Session.CurrentUser;
            _welcome.Text = $"مرحباً بك مجدداً، {u?.FullName ?? u?.Username}";

            try
            {
                var today = Session.Services.Reports.Daily(u, DateTime.Today);
                var yesterday = Session.Services.Reports.Daily(u, DateTime.Today.AddDays(-1));
                string delta = yesterday.TotalSales > 0
                    ? $"{(today.TotalSales - yesterday.TotalSales) / yesterday.TotalSales * 100:+0;-0}٪ من أمس"
                    : "";
                _sales.Value = Fmt.Money(today.TotalSales);
                _sales.Sub = delta;
            }
            catch { _sales.Value = "—"; }

            try
            {
                int n = Session.Services.Inventory.GetNearExpiry().Count;
                _expiry.Value = $"{n} صنف";
                _expiry.Sub = n > 0 ? "عاجل" : "";
            }
            catch { _expiry.Value = "—"; }

            try
            {
                int n = Session.Services.Inventory.GetLowStock().Count;
                _low.Value = $"{n} صنف";
                _low.Sub = n > 0 ? "حرج" : "";
            }
            catch { _low.Value = "—"; }

            try
            {
                _debts.Value = Fmt.Money(Session.Services.Debts.TotalOutstanding());
                _debts.Sub = $"{Session.Services.Debts.WithDebt().Count} عملاء";
            }
            catch { _debts.Value = "—"; }

            try
            {
                bool warn = Session.Services.Backup.NeedsWarning();
                _backupWarn.Height = warn ? 26 : 0;
                _backupWarn.Text = warn ? "⚠ لا توجد نسخة احتياطية حديثة — افتح الإعدادات وأنشئ نسخة الآن." : "";
            }
            catch { }

            try
            {
                _recent.SetSales(Session.Services.Pos.SalesOn(DateTime.Today)
                    .OrderByDescending(s => s.CreatedAt).Take(5).ToList());
            }
            catch { _recent.SetSales(new List<Sale>()); }

            foreach (Control c in Controls) c.Invalidate();
        }
    }

    /// <summary>The "آخر المبيعات" card: a white rounded card listing today's latest sales with status chips.</summary>
    public class SalesListCard : Control
    {
        private IReadOnlyList<Sale> _sales = new List<Sale>();

        public SalesListCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetSales(IReadOnlyList<Sale> sales)
        {
            _sales = sales ?? new List<Sale>();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Background))
                g.FillRectangle(bg, ClientRectangle);

            var card = new RectangleF(2, 2, Width - 4, Height - 4);
            Gfx.FillRounded(g, card, 16, Theme.Surface);
            Gfx.DrawRoundedBorder(g, card, 16, Theme.CardBorder);

            // RTL string format: Near = right edge, Far = left edge.
            var right = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near };
            var left = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Far };

            using (var titleF = new Font(Theme.FontFamily, 13f, FontStyle.Bold))
            using (var tb = new SolidBrush(Theme.TextPrimary))
                g.DrawString("آخر المبيعات", titleF, tb, new RectangleF(card.X + 24, card.Y + 18, card.Width - 48, 26), right);

            float y = card.Y + 58;
            float rowH = 64;

            if (_sales.Count == 0)
            {
                using (var f = new Font(Theme.FontFamily, 10.5f))
                using (var mb = new SolidBrush(Theme.TextMuted))
                    g.DrawString("لا توجد مبيعات اليوم بعد.", f, mb,
                        new RectangleF(card.X + 24, y + 8, card.Width - 48, 24), right);
                return;
            }

            using (var nameF = new Font(Theme.FontFamily, 10.5f, FontStyle.Bold))
            using (var subF = new Font(Theme.FontFamily, 9f))
            using (var amtF = new Font(Theme.FontFamily, 11f, FontStyle.Bold))
            using (var chipF = new Font(Theme.FontFamily, 8.5f, FontStyle.Bold))
            {
                foreach (Sale s in _sales)
                {
                    if (y + rowH > card.Bottom - 10) break;
                    var row = new RectangleF(card.X + 18, y, card.Width - 36, rowH - 10);
                    Gfx.FillRounded(g, row, 12, Color.FromArgb(251, 253, 252));
                    Gfx.DrawRoundedBorder(g, row, 12, Theme.CardBorder);

                    bool returned = s.HasReturn;   // whole or partial — either way it is not a clean sale
                    Color chipBg = returned ? Color.FromArgb(250, 226, 226) : Color.FromArgb(208, 240, 230);
                    Color chipFg = returned ? Theme.Danger : Theme.Primary;

                    // receipt icon chip (right)
                    var iconChip = new RectangleF(row.Right - 54, row.Y + 9, 36, 36);
                    Gfx.FillRounded(g, iconChip, 10, Color.FromArgb(224, 242, 236));
                    Gfx.DrawIcon(g, "receipt", new RectangleF(iconChip.X + 8, iconChip.Y + 8, 20, 20), Theme.Primary);

                    // number + time/type (right of centre)
                    using (var tb = new SolidBrush(Theme.TextPrimary))
                    using (var mb = new SolidBrush(Theme.TextMuted))
                    {
                        g.DrawString("فاتورة #" + s.SaleNumber, nameF, tb,
                            new RectangleF(row.X + 190, row.Y + 8, row.Width - 260, 20), right);
                        string type = s.SaleType == SaleType.Credit ? "آجل" : "نقداً";
                        g.DrawString($"{s.CreatedAt:HH:mm} - {type}", subF, mb,
                            new RectangleF(row.X + 190, row.Y + 30, row.Width - 260, 16), right);
                    }

                    // amount + status chip (left)
                    using (var ab = new SolidBrush(returned ? Theme.Danger : Theme.TextPrimary))
                        g.DrawString(Fmt.Money(s.Total), amtF, ab, new RectangleF(row.X + 14, row.Y + 7, 160, 20), left);

                    string chipText = returned ? "مرجع" : "مكتمل";
                    var chip = new RectangleF(row.X + 14, row.Y + 30, 56, 20);
                    Gfx.FillRounded(g, chip, 9, chipBg);
                    using (var cb = new SolidBrush(chipFg))
                    {
                        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(chipText, chipF, cb, chip, center);
                    }

                    y += rowH;
                }
            }
        }
    }
}
