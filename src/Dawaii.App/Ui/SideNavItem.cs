using System;
using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>A sidebar navigation row (RTL): icon on the right, label to its left, mint pill when active.</summary>
    public class SideNavItem : Control
    {
        private readonly string _icon;
        private bool _active, _hover;

        public event EventHandler Activated;

        public bool Active
        {
            get => _active;
            set { _active = value; Invalidate(); }
        }

        public SideNavItem(string text, string icon)
        {
            _icon = icon;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 46;
            Cursor = Cursors.Hand;
            Font = Theme.Base(11.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Activated?.Invoke(this, EventArgs.Empty); base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(8, 3, Width - 16, Height - 6);
            if (_active)
                Gfx.FillRounded(g, r, 12, Color.FromArgb(206, 236, 226));   // mint pill
            else if (_hover)
                Gfx.FillRounded(g, r, 12, Color.FromArgb(240, 246, 244));

            Color fg = _active ? Theme.Primary : Theme.TextPrimary;
            float iconSize = 22;
            var iconRect = new RectangleF(Width - 20 - iconSize, (Height - iconSize) / 2f, iconSize, iconSize);
            Gfx.DrawIcon(g, _icon, iconRect, fg);

            var textRect = new Rectangle(20, 0, Width - 60, Height);
            TextRenderer.DrawText(g, Text, _active ? Theme.Base(11.5f, FontStyle.Bold) : Font, textRect, fg,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft);
        }
    }
}
