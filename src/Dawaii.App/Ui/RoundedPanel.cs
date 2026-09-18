using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// A rounded panel that can host child controls — used for the design's cards. Paints a soft
    /// shadow, a solid or vertical-gradient fill, and an optional border. Set <see cref="Inset"/> to
    /// leave room for the shadow around the card.
    /// </summary>
    public class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; } = 16;
        public int Inset { get; set; } = 10;               // margin reserved for the shadow
        public bool ShowShadow { get; set; } = true;
        public Color FillColor { get; set; } = Color.White;
        public Color BorderColor { get; set; } = Color.Empty;
        public bool UseGradient { get; set; } = false;
        public Color GradientTop { get; set; } = Color.White;
        public Color GradientBottom { get; set; } = Color.White;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        /// <summary>The white/gradient content rectangle (inside the shadow inset).</summary>
        public Rectangle CardBounds =>
            new Rectangle(Inset, Inset, Width - Inset * 2, Height - Inset * 2);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Clear with the parent's colour so the rounded corners blend with the page.
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Background))
                g.FillRectangle(bg, ClientRectangle);

            RectangleF card = CardBounds;
            if (ShowShadow) Gfx.DrawShadow(g, card, CornerRadius);

            using (var path = Gfx.RoundedRect(card, CornerRadius))
            {
                if (UseGradient)
                    using (var b = new LinearGradientBrush(card, GradientTop, GradientBottom, LinearGradientMode.Vertical))
                        g.FillPath(b, path);
                else
                    using (var b = new SolidBrush(FillColor))
                        g.FillPath(b, path);

                if (BorderColor != Color.Empty)
                    using (var pen = new Pen(BorderColor, 1f))
                        g.DrawPath(pen, path);
            }

            base.OnPaint(e); // let instance Paint handlers draw on top (e.g. branding text)
        }
    }
}
