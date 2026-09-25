using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Modules;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// Main window (Figma design): light sidebar on the right (logo, icon nav with mint active pill,
    /// user card + logout at the bottom), white top bar (module title, date, alerts), content area.
    /// Navigation stays role-based (D-15): Admin = management, Employee = selling. Between the two sits
    /// the "موظف ذو امتيازات", who is served out of the shared block at the end: الأصناف والمخزون (V1.8)
    /// and العملاء والديون (V2.0) are listed for them as well as for the manager, and each screen then
    /// enforces the rest — a privileged employee runs the stockroom and the counter accounts, but the
    /// manager's own entries (deleting an account holder, raising a debt by hand) stay hidden and refused.
    /// </summary>
    public class MainForm : BaseForm
    {
        private Panel _sidebar, _content, _topbar;
        private Label _topTitle;
        private readonly List<SideNavItem> _navItems = new List<SideNavItem>();
        private ModuleControl _current;

        public MainForm()
        {
            BuildUi();
            Navigate("الرئيسية", new HomeModule());
        }

        private void BuildUi()
        {
            Text = "دوائي — نظام إدارة الصيدلية";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1100, 700);
            BackColor = Theme.Background;

            // Sidebar (Dock.Left renders on the RIGHT because RightToLeftLayout mirrors the form).
            _sidebar = new Panel { Dock = DockStyle.Left, Width = 232, BackColor = Theme.Surface };
            _sidebar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.CardBorder))
                    e.Graphics.DrawLine(pen, 0, 0, 0, _sidebar.Height); // hairline towards content
            };

            _topbar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Surface };
            _topbar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.CardBorder))
                    e.Graphics.DrawLine(pen, 0, _topbar.Height - 1, _topbar.Width, _topbar.Height - 1);
            };

            _topTitle = new Label
            {
                Font = Theme.Base(14f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Right,
                Width = 320,
                Padding = new Padding(0, 0, 24, 0)
            };

            var dateLabel = new Label
            {
                Text = FormatArabicDate(DateTime.Today),
                Font = Theme.Base(10.5f),
                ForeColor = Theme.TextMuted,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Left,
                Width = 220,
                Padding = new Padding(18, 0, 0, 0)
            };

            var bell = new IconButton("bell") { Dock = DockStyle.Left, Width = 44 };
            bell.Clicked += (s, e) => ShowAlerts();

            _topbar.Controls.Add(dateLabel);
            _topbar.Controls.Add(bell);
            _topbar.Controls.Add(_topTitle);

            _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };

            Controls.Add(_content);
            Controls.Add(_topbar);
            Controls.Add(_sidebar);

            BuildSidebar();
        }

        // ---------------- sidebar ----------------

        private void BuildSidebar()
        {
            _sidebar.Controls.Clear();
            _navItems.Clear();

            // Bottom: user card + logout.
            _sidebar.Controls.Add(BuildUserCard());

            // Middle nav — added bottom-up (each Dock.Top; last added shows first).
            AddDialogItem("تغيير كلمة المرور", "key", () => new ChangePasswordForm().ShowDialog(this));

            if (Session.IsAdmin)
            {
                AddNavItem("الإعدادات والنسخ", "gear", () => new SettingsModule());
                AddNavItem("قرب الانتهاء", "calendar", () => new ExpiryModule());
                AddNavItem("المشتريات", "cart", () => new PurchasesModule());
                AddDialogItem("تقرير المشتريات", "chart", () => new PurchaseReportForm().ShowDialog(this));
                AddNavItem("إدارة الموظفين", "users", () => new UsersModule());
                AddNavItem("شؤون الموظفين", "receipt", () => new EmployeeAffairsModule());
                AddNavItem("التقارير", "chart", () => new ReportsModule());
                AddNavItem("تقرير الوردية", "receipt", () => new ShiftReportModule());
            }
            else
            {
                // Looking after the stock includes watching what is about to expire.
                if (Session.CanManageInventory)
                    AddNavItem("قرب الانتهاء", "calendar", () => new ExpiryModule());

                AddDialogItem("مصروفاتي", "cash", () =>
                {
                    using (var f = new ExpenseForm(Session.CurrentUser.Id,
                        Session.CurrentUser.FullName ?? Session.CurrentUser.Username))
                        f.ShowDialog(this);
                });
                // Own day only — the form reads the user from the session and the service refuses
                // any other employee's id, so a cashier can never see a colleague's invoices.
                AddDialogItem("مبيعاتي اليوم", "receipt", () =>
                {
                    using (var f = EmployeeSalesDetailForm.ForCurrentUser())
                        f.ShowDialog(this);
                });
                AddDialogItem("إرجاع فاتورة", "return", () => new ReturnSaleForm().ShowDialog(this));
                AddDialogItem("تسجيل مشترى", "cart", () =>
                {
                    using (var f = new PurchaseForm())
                        f.ShowDialog(this);
                });
                AddNavItem("نقطة البيع", "pos", () => new PosModule());
            }

            // The ledger is no longer the manager's alone (V2.0): a "موظف ذو امتيازات" runs the counter
            // accounts too — opening a customer's statement, adding a customer, taking a repayment. What
            // stays the manager's — editing or deleting an account holder, adding a debt by hand — is
            // hidden inside the screen and refused by the service, so the same screen serves both roles.
            if (Session.CanManageCustomers)
                AddNavItem("العملاء والديون", "cash", () => new CustomersModule());

            // Companies and their orders: the manager and the "موظف ذو امتيازات" (V2.1).
            if (Session.CanManagePurchasing)
                AddNavItem("الموردون والمشتريات", "cart", () => new SuppliersModule());

            // The stockroom belongs to the manager and the "موظف ذو امتيازات" — the same person who
            // files the order unpacks the boxes onto the shelf. A plain cashier works from the POS
            // screen and never sees it (V1.8, restored in V2.3).
            if (Session.CanManageInventory)
            {
                // Prices are raised by the same people who enter deliveries (V2.3): a multiplier over
                // the current selling price, rounded to a sayable figure, or a price typed by hand —
                // previewed, then applied. Listed just below the stockroom.
                AddNavItem("زيادة الأسعار", "cash", () => new PricingModule());
                AddNavItem("الأصناف والمخزون", "box", () => new ItemsModule());
            }

            // Reprinting a receipt is everyone's (V2.4): it only reads, and the customer who lost
            // their copy is standing at whichever counter is free. Listed just under the home screen.
            AddNavItem("إعادة طباعة فاتورة", "receipt", () => new ReprintModule());

            AddNavItem("الرئيسية", "home", () => new HomeModule());

            // Header: logo + product name (topmost).
            _sidebar.Controls.Add(BuildSidebarHeader());
        }

        private Control BuildSidebarHeader()
        {
            // Wordmark only — the logo image lives on the login screen (kept clean here per design request).
            var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.Surface };
            var name = new Label
            {
                Text = "دوائي",
                Font = Theme.Title(19f),
                ForeColor = Theme.Primary,
                BackColor = Theme.Surface,
                AutoSize = false,
                TextAlign = ContentAlignment.BottomRight,
                Bounds = new Rectangle(0, 18, 200, 30)
            };
            var sub = new Label
            {
                Text = "نظام إدارة الصيدلية",
                Font = Theme.Base(9f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Surface,
                AutoSize = false,
                TextAlign = ContentAlignment.TopRight,
                Bounds = new Rectangle(0, 50, 200, 20)
            };
            header.Resize += (s, e) =>
            {
                name.Bounds = new Rectangle(header.Width - 224, 20, 200, 30);
                sub.Bounds = new Rectangle(header.Width - 224, 52, 200, 18);
            };
            header.Controls.Add(name);
            header.Controls.Add(sub);
            return header;
        }

        private Control BuildUserCard()
        {
            var card = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = Theme.Surface };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.CardBorder))
                    e.Graphics.DrawLine(pen, 12, 0, card.Width - 12, 0);

                // avatar circle with initial
                User u = Session.CurrentUser;
                string initial = string.IsNullOrEmpty(u?.FullName ?? u?.Username) ? "؟" : (u.FullName ?? u.Username).Substring(0, 1);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var avatar = new RectangleF(card.Width - 58, 18, 42, 42);
                using (var b = new SolidBrush(Color.FromArgb(206, 236, 226))) g.FillEllipse(b, avatar);
                using (var f = new Font(Theme.FontFamily, 14f, FontStyle.Bold))
                using (var tb = new SolidBrush(Theme.Primary))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(initial, f, tb, avatar, sf);
                }

                string nameText = u?.FullName ?? u?.Username ?? "";
                string roleText = u == null ? "" : u.IsAdmin ? "المدير المسؤول" : RoleLabels.Of(u.Role);
                var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near }; // Near = right
                using (var nf = new Font(Theme.FontFamily, 10.5f, FontStyle.Bold))
                using (var rf = new Font(Theme.FontFamily, 9f))
                using (var nb = new SolidBrush(Theme.TextPrimary))
                using (var mb = new SolidBrush(Theme.TextMuted))
                {
                    g.DrawString(nameText, nf, nb, new RectangleF(50, 20, card.Width - 116, 20), rtl);
                    g.DrawString(roleText, rf, mb, new RectangleF(50, 42, card.Width - 116, 16), rtl);
                }
            };

            var logout = new IconButton("logout") { Location = new Point(10, 22), IconColor = Theme.Danger };
            logout.Clicked += (s, e) => Logout();
            var tip = new ToolTip();
            tip.SetToolTip(logout, "تسجيل الخروج");
            card.Controls.Add(logout);
            return card;
        }

        private void AddNavItem(string text, string icon, Func<ModuleControl> factory)
        {
            var item = new SideNavItem(text, icon) { Dock = DockStyle.Top };
            item.Activated += (s, e) => Navigate(text, factory(), item);
            _sidebar.Controls.Add(item);
            _navItems.Add(item);
        }

        private void AddDialogItem(string text, string icon, Action open)
        {
            var item = new SideNavItem(text, icon) { Dock = DockStyle.Top };
            item.Activated += (s, e) => open();
            _sidebar.Controls.Add(item);
        }

        // ---------------- navigation ----------------

        public void Navigate(string title, ModuleControl module, SideNavItem source = null)
        {
            // Every screen change re-reads the signed-in user, so revoking someone's access takes effect
            // on their counter within one click instead of waiting for them to log out (V2.3). A role
            // change also has to redraw the sidebar, or a demoted employee keeps the buttons for screens
            // the service layer will now refuse them.
            Role roleBefore = Session.CurrentUser?.Role ?? Role.Cashier;
            if (!Session.Refresh())
            {
                module?.Dispose();
                Msg.Warn("تم إيقاف حسابك أو حذفه. سيتم تسجيل الخروج.");
                Logout();
                return;
            }
            if (Session.CurrentUser.Role != roleBefore) { BuildSidebar(); Msg.Info("تم تغيير صلاحياتك."); }

            foreach (SideNavItem it in _navItems) it.Active = ReferenceEquals(it, source) || (source == null && it.Text == title);
            _topTitle.Text = title == "الرئيسية" ? "لوحة التحكم" : title;

            _content.SuspendLayout();
            if (_current != null) { _content.Controls.Remove(_current); _current.Dispose(); }
            _current = module;
            _content.Controls.Add(module);
            _content.ResumeLayout();
            module.OnActivated();
        }

        /// <summary>Opens the POS module from other screens (e.g. the dashboard's "new invoice" button).</summary>
        public void OpenPos() => Navigate("نقطة البيع", new PosModule());

        private void ShowAlerts()
        {
            try
            {
                int nearExpiry = Session.Services.Inventory.GetNearExpiry().Count;
                int low = Session.Services.Inventory.GetLowStock().Count;
                bool backup = Session.Services.Backup.NeedsWarning();
                Msg.Info(
                    $"أصناف قريبة الانتهاء: {nearExpiry}\n" +
                    $"أصناف منخفضة المخزون: {low}\n" +
                    (backup ? "⚠ لا توجد نسخة احتياطية حديثة (آخر 3 أيام)." : "النسخ الاحتياطي محدث."),
                    "التنبيهات");
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private static string FormatArabicDate(DateTime d)
        {
            string[] days = { "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
            string[] months = { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };
            return $"{days[(int)d.DayOfWeek]}، {d.Day} {months[d.Month - 1]} {d.Year}";
        }

        private void Logout()
        {
            Modules.PosModule.ResetCarts();   // pending invoices are this cashier's, not the next one's
            // A delivery window left open — possibly just minimized, and easy to forget — belongs to
            // the person signing out. It goes before the drafts, so it cannot file one on its way.
            PurchaseInvoiceForm.CloseOpen();
            PurchaseDrafts.Clear();           // and so are half-typed deliveries
            Modules.PricingModule.ResetPending();   // and prices calculated but never applied
            Session.SignOut();
            DialogResult = DialogResult.Retry; // Program shows the login screen again
            Close();
        }
    }
}
