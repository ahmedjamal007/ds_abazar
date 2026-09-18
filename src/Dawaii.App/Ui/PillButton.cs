using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>A rounded button: filled teal (primary) or white with a border (outline/secondary).</summary>
    public class PillButton : Button
    {
        public int CornerRadius { get; set; } = 12;
        public Color FillColor { get; set; } = Theme.Primary;
        public Color SurfaceColor { get; set; } = Color.Empty;   // empty = use parent's back colour
        public bool Outline { get; set; } = false;
        private bool _hover;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Font = Theme.Base(11f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(System.EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(System.EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = SurfaceColor != Color.Empty ? SurfaceColor
                : (Parent != null && Parent.BackColor.A == 255 ? Parent.BackColor : Theme.Background);
            using (var bg = new SolidBrush(surface))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            if (Outline)
            {
                Gfx.FillRounded(g, r, CornerRadius, _hover ? Color.FromArgb(240, 247, 244) : Theme.Surface);
                Gfx.DrawRoundedBorder(g, r, CornerRadius, Theme.Primary, 1.2f);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), Theme.Primary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                Color fill = Enabled ? (_hover ? ControlPaint.Dark(FillColor, 0.03f) : FillColor)
                                     : Color.FromArgb(180, FillColor);
                Gfx.FillRounded(g, r, CornerRadius, fill);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
