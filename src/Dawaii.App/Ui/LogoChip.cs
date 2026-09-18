using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// The logo presented as a rounded "app icon" tile (like the Figma): a white rounded chip with a
    /// soft shadow and the logo image clipped to rounded corners inside it.
    /// </summary>
    public class LogoChip : Control
    {
        public int CornerRadius { get; set; } = 16;
        public bool ShowShadow { get; set; } = true;

        public LogoChip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(64, 64); // background is painted with the parent's colour in OnPaint
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
                g.FillRectangle(bg, ClientRectangle);

            var chip = new RectangleF(4, 3, Width - 8, Height - 8);
            if (ShowShadow) Gfx.DrawShadow(g, chip, CornerRadius, depth: 5, alpha: 12);
            Gfx.FillRounded(g, chip, CornerRadius, Color.White);
            Gfx.DrawRoundedBorder(g, chip, CornerRadius, Theme.CardBorder);

            Image logo = AppImages.Logo;
            if (logo == null) return;

            // Clip the (square, opaque-background) logo to slightly smaller rounded corners.
            var inner = new RectangleF(chip.X + 3, chip.Y + 3, chip.Width - 6, chip.Height - 6);
            using (var clip = Gfx.RoundedRect(inner, CornerRadius - 4))
            {
                var old = g.Clip;
                g.SetClip(clip, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(logo, inner);
                g.Clip = old;
            }
        }
    }
}
