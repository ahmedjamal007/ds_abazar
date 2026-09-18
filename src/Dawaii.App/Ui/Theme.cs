using System;
using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>Central colours, fonts and small styling helpers for a consistent Arabic RTL look.</summary>
    public static class Theme
    {
        // Calm medical teal/green palette (aligned to the Figma design).
        public static readonly Color Primary      = Color.FromArgb(15, 122, 95);    // brand teal (buttons/headings)
        public static readonly Color PrimaryDark  = Color.FromArgb(20, 92, 74);
        public static readonly Color GradientTop   = Color.FromArgb(232, 242, 238);  // branding card gradient start
        public static readonly Color GradientBottom= Color.FromArgb(22, 96, 78);     // branding card gradient end
        public static readonly Color Accent       = Color.FromArgb(255, 152, 0);   // amber (warnings)
        public static readonly Color Danger       = Color.FromArgb(211, 47, 47);
        public static readonly Color Background    = Color.FromArgb(238, 243, 241);  // page background
        public static readonly Color Surface       = Color.White;
        public static readonly Color CardBorder    = Color.FromArgb(228, 233, 231);
        public static readonly Color InputBg       = Color.FromArgb(247, 250, 249);
        public static readonly Color InputBorder   = Color.FromArgb(221, 228, 225);
        public static readonly Color TextPrimary   = Color.FromArgb(38, 44, 42);
        public static readonly Color TextMuted     = Color.FromArgb(120, 134, 130);

        // A font that renders Arabic well and exists on Windows 7 SP1+.
        public const string FontFamily = "Segoe UI";

        public static Font Base(float size = 11f, FontStyle style = FontStyle.Regular)
            => new Font(FontFamily, size, style);

        public static Font Title(float size = 18f)
            => new Font(FontFamily, size, FontStyle.Bold);

        public static void StylePrimaryButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Primary;
            b.ForeColor = Color.White;
            b.Font = Base(12f, FontStyle.Bold);
            b.Height = 40;
            b.Cursor = Cursors.Hand;
        }

        public static void StyleSecondaryButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Primary;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = Surface;
            b.ForeColor = Primary;
            b.Font = Base(11f, FontStyle.Bold);
            b.Height = 38;
            b.Cursor = Cursors.Hand;
        }

        /// <summary>The standard toolbar/dialog action button — rounded per the design system.</summary>
        /// <summary>
        /// The application's standard button. Every save, payment, delete and print goes through here.
        ///
        /// The handler is guarded against re-entry (V2.3). Nothing in the app used to disable a button
        /// while its work ran, and several handlers pump a nested message loop before they finish — a
        /// print dialog, a child form, GDI printing — during which a click queued by an impatient user
        /// is dispatched. On the POS that meant a second complete sale, with its own stock decrements
        /// and its own debt charge, because the cart is not cleared until after printing.
        ///
        /// The button is also visibly disabled for the duration, so a slow operation looks like it is
        /// working rather than like it was ignored.
        /// </summary>
        public static Button ActionButton(string text, Action onClick, bool primary = false, int width = 150)
        {
            var b = new PillButton
            {
                Text = text, Width = width, Height = 40,
                Outline = !primary, CornerRadius = 10,
                Margin = new Padding(5, 6, 5, 6)
            };

            bool running = false;
            b.Click += (s, e) =>
            {
                if (running) return;
                running = true;
                b.Enabled = false;
                try { onClick(); }
                finally
                {
                    running = false;
                    // The handler may have closed and disposed the form it lived on.
                    if (!b.IsDisposed) b.Enabled = true;
                }
            };
            return b;
        }

        /// <summary>Modern flat table: light header, roomy rows, mint selection (per the design).</summary>
        public static void StyleGrid(DataGridView g)
        {
            // Columns are never sortable (V2.3). Fourteen screens find the selected record by mapping
            // the grid's row index into the parallel List<T> they bound — safe only while the view order
            // and the list order are the same. Clicking a header would also throw outright, because a
            // plain List<T> is not an IBindingList, so nothing is lost by saying so explicitly here
            // rather than leaving it to hold by accident.
            g.ColumnAdded += (s, e) => e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;

            g.BackgroundColor = Surface;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.GridColor = Color.FromArgb(238, 242, 240);
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 249);
            g.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
            // Without these, the current column's header flips to the system-blue selection colour.
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(247, 250, 249);
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMuted;
            g.ColumnHeadersDefaultCellStyle.Font = Base(10.5f, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 42;
            g.RowTemplate.Height = 38;
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToResizeRows = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.DefaultCellStyle.BackColor = Surface;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 240, 231);
            g.DefaultCellStyle.SelectionForeColor = TextPrimary;
            g.DefaultCellStyle.Font = Base(10.5f);
            g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = Surface;
            g.RightToLeft = RightToLeft.Yes;
        }

        /// <summary>Standard page header (dark title + muted subtitle) used by the modules.</summary>
        public static Control PageHeader(string title, string subtitle = null)
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = subtitle == null ? 52 : 74, BackColor = Background };
            if (subtitle != null)
                panel.Controls.Add(new Label
                {
                    Text = subtitle, Font = Base(10f), ForeColor = TextMuted, BackColor = Background,
                    Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.TopRight
                });
            panel.Controls.Add(new Label
            {
                Text = title, Font = Title(18f), ForeColor = TextPrimary, BackColor = Background,
                Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleRight
            });
            return panel;
        }
    }
}
