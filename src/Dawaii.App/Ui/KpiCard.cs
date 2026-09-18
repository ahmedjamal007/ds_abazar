using System.Drawing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>A dashboard KPI tile: icon, label, big value and a small sub-note. Teal is filled; others are tinted.</summary>
    public class KpiCard : Control
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public string Sub { get; set; } = "";
        public string IconName { get; set; } = "cash";
        public Color Accent { get; set; } = Theme.Primary;
        public bool Filled { get; set; } = false;   // true = solid teal card with white text

        public KpiCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(250, 148);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Background))
                g.FillRectangle(bg, ClientRectangle);

            var card = new RectangleF(2, 2, Width - 4, Height - 4);
            Color fill = Filled ? Accent : Color.White;
            Color border = Filled ? Accent : Color.FromArgb(60, Accent);
            Color text = Filled ? Color.White : Theme.TextPrimary;
            Color muted = Filled ? Color.FromArgb(210, 255, 255, 255) : Theme.TextMuted;
            Color accentText = Filled ? Color.White : Accent;

            Gfx.FillRounded(g, card, 16, fill);
            Gfx.DrawRoundedBorder(g, card, 16, border, 1.4f);

            // RTL string format: Near = right edge, Far = left edge.
            var right = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near };
            var left = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Far };

            // icon chip (top-left)
            var chip = new RectangleF(16, 16, 44, 44);
            Gfx.FillRounded(g, chip, 12, Filled ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, Accent));
            Gfx.DrawIcon(g, IconName, new RectangleF(chip.X + 11, chip.Y + 11, 22, 22), accentText, 2f);

            using (var labelF = new Font(Theme.FontFamily, 10.5f))
            using (var valueF = new Font(Theme.FontFamily, 21f, FontStyle.Bold))
            using (var subF = new Font(Theme.FontFamily, 9.5f, FontStyle.Bold))
            using (var tb = new SolidBrush(text))
            using (var mb = new SolidBrush(muted))
            using (var ab = new SolidBrush(accentText))
            {
                g.DrawString(Label, labelF, mb, new RectangleF(chip.Right + 8, 22, Width - chip.Right - 26, 20), right);
                g.DrawString(Sub, subF, ab, new RectangleF(16, 66, Width * 0.5f, 18), left);   // under the chip
                g.DrawString(Value, valueF, tb, new RectangleF(16, 92, Width - 34, 36), right);
            }
        }
    }
}
