using System;
using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>A small square icon button (line-art icon, subtle hover circle).</summary>
    public class IconButton : Control
    {
        private readonly string _icon;
        private bool _hover;
        public Color IconColor { get; set; } = Theme.TextMuted;
        public event EventHandler Clicked;

        public IconButton(string icon, int size = 38)
        {
            _icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(size, size);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Clicked?.Invoke(this, EventArgs.Empty); base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
                g.FillRectangle(bg, ClientRectangle);
            if (_hover)
                Gfx.FillRounded(g, new RectangleF(1, 1, Width - 2, Height - 2), 10, Color.FromArgb(238, 244, 242));
            float pad = Width * 0.28f;
            Gfx.DrawIcon(g, _icon, new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2), IconColor);
        }
    }
}
