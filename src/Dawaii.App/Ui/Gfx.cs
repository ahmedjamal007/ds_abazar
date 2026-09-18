using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Dawaii.App.Ui
{
    /// <summary>Small GDI+ helpers for the rounded, card-based look from the design.</summary>
    public static class Gfx
    {
        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); path.CloseFigure(); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = RoundedRect(r, radius))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void DrawRoundedBorder(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = RoundedRect(r, radius))
            using (var pen = new Pen(color, width))
                g.DrawPath(pen, p);
        }

        /// <summary>A soft drop shadow behind a rounded card (drawn as fading concentric outlines).</summary>
        public static void DrawShadow(Graphics g, RectangleF card, float radius, int depth = 8, int alpha = 9)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = depth; i >= 1; i--)
            {
                var r = new RectangleF(card.X - i, card.Y - i + 2, card.Width + i * 2, card.Height + i * 2);
                using (var p = RoundedRect(r, radius + i))
                using (var pen = new Pen(Color.FromArgb(alpha, 0, 40, 30), 2))
                    g.DrawPath(pen, p);
            }
        }

        // ---- minimal line icons (replace the Material icon names in the mockup) ----

        public static void PersonIcon(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, 1.8f))
            {
                float cx = r.X + r.Width / 2f;
                float head = r.Width * 0.30f;
                g.DrawEllipse(pen, cx - head / 2, r.Y + r.Height * 0.12f, head, head);
                var body = new RectangleF(cx - r.Width * 0.32f, r.Y + r.Height * 0.52f, r.Width * 0.64f, r.Height * 0.5f);
                using (var path = new GraphicsPath())
                {
                    path.AddArc(body.X, body.Y, body.Width, body.Height, 180, 180);
                    g.DrawPath(pen, path);
                }
            }
        }

        public static void LockIcon(Graphics g, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, 1.8f))
            {
                var body = new RectangleF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.45f, r.Width * 0.64f, r.Height * 0.45f);
                using (var p = RoundedRect(body, 3f)) g.DrawPath(pen, p);
                float aw = r.Width * 0.36f;
                g.DrawArc(pen, r.X + r.Width / 2 - aw / 2, r.Y + r.Height * 0.20f, aw, aw, 180, 180);
            }
        }

        /// <summary>Draws a simple line-art icon by name, fitted to <paramref name="r"/>.</summary>
        public static void DrawIcon(Graphics g, string name, RectangleF r, Color c, float w = 1.9f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(c))
            {
                float x = r.X, y = r.Y, W = r.Width, H = r.Height;
                switch (name)
                {
                    case "home":
                        g.DrawLines(pen, new[] { new PointF(x, y + H * 0.5f), new PointF(x + W * 0.5f, y + H * 0.08f), new PointF(x + W, y + H * 0.5f) });
                        g.DrawRectangle(pen, x + W * 0.18f, y + H * 0.45f, W * 0.64f, H * 0.5f);
                        break;
                    case "pos": // cash register
                        g.DrawRectangle(pen, x + W * 0.12f, y + H * 0.35f, W * 0.76f, H * 0.5f);
                        g.DrawRectangle(pen, x + W * 0.28f, y + H * 0.15f, W * 0.44f, H * 0.22f);
                        g.DrawLine(pen, x + W * 0.24f, y + H * 0.55f, x + W * 0.5f, y + H * 0.55f);
                        break;
                    case "box": // inventory
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.5f, y + H * 0.08f), new PointF(x + W * 0.92f, y + H * 0.3f), new PointF(x + W * 0.92f, y + H * 0.72f), new PointF(x + W * 0.5f, y + H * 0.94f), new PointF(x + W * 0.08f, y + H * 0.72f), new PointF(x + W * 0.08f, y + H * 0.3f), new PointF(x + W * 0.5f, y + H * 0.08f) });
                        g.DrawLine(pen, x + W * 0.08f, y + H * 0.3f, x + W * 0.5f, y + H * 0.52f);
                        g.DrawLine(pen, x + W * 0.92f, y + H * 0.3f, x + W * 0.5f, y + H * 0.52f);
                        g.DrawLine(pen, x + W * 0.5f, y + H * 0.52f, x + W * 0.5f, y + H * 0.94f);
                        break;
                    case "chart": // reports
                        g.DrawLine(pen, x + W * 0.12f, y + H * 0.1f, x + W * 0.12f, y + H * 0.9f);
                        g.DrawLine(pen, x + W * 0.12f, y + H * 0.9f, x + W * 0.9f, y + H * 0.9f);
                        g.DrawLine(pen, x + W * 0.32f, y + H * 0.7f, x + W * 0.32f, y + H * 0.55f);
                        g.DrawLine(pen, x + W * 0.52f, y + H * 0.7f, x + W * 0.52f, y + H * 0.35f);
                        g.DrawLine(pen, x + W * 0.72f, y + H * 0.7f, x + W * 0.72f, y + H * 0.2f);
                        break;
                    case "gear":
                        g.DrawEllipse(pen, x + W * 0.32f, y + H * 0.32f, W * 0.36f, H * 0.36f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            float cx = x + W / 2, cy = y + H / 2;
                            g.DrawLine(pen, cx + (float)Math.Cos(a) * W * 0.30f, cy + (float)Math.Sin(a) * H * 0.30f,
                                cx + (float)Math.Cos(a) * W * 0.46f, cy + (float)Math.Sin(a) * H * 0.46f);
                        }
                        break;
                    case "users":
                        g.DrawEllipse(pen, x + W * 0.30f, y + H * 0.14f, W * 0.28f, H * 0.28f);
                        using (var p2 = new GraphicsPath()) { p2.AddArc(x + W * 0.16f, y + H * 0.5f, W * 0.56f, H * 0.6f, 180, 180); g.DrawPath(pen, p2); }
                        g.DrawEllipse(pen, x + W * 0.60f, y + H * 0.22f, W * 0.22f, H * 0.22f);
                        break;
                    case "tag": // pricing
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.52f, y + H * 0.1f), new PointF(x + W * 0.9f, y + H * 0.48f), new PointF(x + W * 0.48f, y + H * 0.9f), new PointF(x + W * 0.1f, y + H * 0.52f), new PointF(x + W * 0.1f, y + H * 0.1f), new PointF(x + W * 0.52f, y + H * 0.1f) });
                        g.FillEllipse(brush, x + W * 0.22f, y + H * 0.22f, W * 0.1f, H * 0.1f);
                        break;
                    case "calendar":
                        g.DrawRectangle(pen, x + W * 0.12f, y + H * 0.18f, W * 0.76f, H * 0.72f);
                        g.DrawLine(pen, x + W * 0.12f, y + H * 0.36f, x + W * 0.88f, y + H * 0.36f);
                        g.DrawLine(pen, x + W * 0.32f, y + H * 0.08f, x + W * 0.32f, y + H * 0.26f);
                        g.DrawLine(pen, x + W * 0.68f, y + H * 0.08f, x + W * 0.68f, y + H * 0.26f);
                        break;
                    case "cash":
                        g.DrawRectangle(pen, x + W * 0.08f, y + H * 0.28f, W * 0.84f, H * 0.44f);
                        g.DrawEllipse(pen, x + W * 0.4f, y + H * 0.38f, W * 0.2f, H * 0.24f);
                        break;
                    case "cart":
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.08f, y + H * 0.14f), new PointF(x + W * 0.24f, y + H * 0.14f), new PointF(x + W * 0.36f, y + H * 0.66f), new PointF(x + W * 0.84f, y + H * 0.66f), new PointF(x + W * 0.92f, y + H * 0.3f), new PointF(x + W * 0.28f, y + H * 0.3f) });
                        g.FillEllipse(brush, x + W * 0.4f, y + H * 0.8f, W * 0.11f, H * 0.11f);
                        g.FillEllipse(brush, x + W * 0.7f, y + H * 0.8f, W * 0.11f, H * 0.11f);
                        break;
                    case "receipt":
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.2f, y + H * 0.08f), new PointF(x + W * 0.2f, y + H * 0.92f), new PointF(x + W * 0.35f, y + H * 0.82f), new PointF(x + W * 0.5f, y + H * 0.92f), new PointF(x + W * 0.65f, y + H * 0.82f), new PointF(x + W * 0.8f, y + H * 0.92f), new PointF(x + W * 0.8f, y + H * 0.08f), new PointF(x + W * 0.2f, y + H * 0.08f) });
                        g.DrawLine(pen, x + W * 0.32f, y + H * 0.32f, x + W * 0.68f, y + H * 0.32f);
                        g.DrawLine(pen, x + W * 0.32f, y + H * 0.5f, x + W * 0.68f, y + H * 0.5f);
                        break;
                    case "return":
                        using (var p3 = new GraphicsPath()) { p3.AddArc(x + W * 0.2f, y + H * 0.25f, W * 0.6f, H * 0.6f, 30, 260); g.DrawPath(pen, p3); }
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.2f, y + H * 0.2f), new PointF(x + W * 0.22f, y + H * 0.5f), new PointF(x + W * 0.5f, y + H * 0.42f) });
                        break;
                    case "bell":
                        using (var p4 = new GraphicsPath()) { p4.AddArc(x + W * 0.2f, y + H * 0.14f, W * 0.6f, H * 0.7f, 180, 180); g.DrawPath(pen, p4); }
                        g.DrawLine(pen, x + W * 0.14f, y + H * 0.7f, x + W * 0.86f, y + H * 0.7f);
                        g.DrawArc(pen, x + W * 0.4f, y + H * 0.72f, W * 0.2f, H * 0.18f, 0, 180);
                        break;
                    case "search":
                        g.DrawEllipse(pen, x + W * 0.14f, y + H * 0.14f, W * 0.5f, H * 0.5f);
                        g.DrawLine(pen, x + W * 0.6f, y + H * 0.6f, x + W * 0.88f, y + H * 0.88f);
                        break;
                    case "logout":
                        g.DrawArc(pen, x + W * 0.1f, y + H * 0.12f, W * 0.6f, H * 0.76f, 300, 120);
                        g.DrawArc(pen, x + W * 0.1f, y + H * 0.12f, W * 0.6f, H * 0.76f, 60, -120);
                        g.DrawLine(pen, x + W * 0.5f, y + H * 0.5f, x + W * 0.95f, y + H * 0.5f);
                        g.DrawLines(pen, new[] { new PointF(x + W * 0.78f, y + H * 0.33f), new PointF(x + W * 0.95f, y + H * 0.5f), new PointF(x + W * 0.78f, y + H * 0.67f) });
                        break;
                    case "key":
                        g.DrawEllipse(pen, x + W * 0.12f, y + H * 0.3f, W * 0.4f, H * 0.4f);
                        g.DrawLine(pen, x + W * 0.48f, y + H * 0.5f, x + W * 0.9f, y + H * 0.5f);
                        g.DrawLine(pen, x + W * 0.8f, y + H * 0.5f, x + W * 0.8f, y + H * 0.66f);
                        break;
                    default:
                        g.DrawEllipse(pen, x + W * 0.2f, y + H * 0.2f, W * 0.6f, H * 0.6f);
                        break;
                }
            }
        }

        public static void EyeIcon(Graphics g, RectangleF r, Color c, bool open)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, 1.6f))
            {
                float cy = r.Y + r.Height / 2f;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(r.X, cy - r.Height * 0.35f, r.Width, r.Height * 0.7f, 20, 140);
                    path.AddArc(r.X, cy - r.Height * 0.35f, r.Width, r.Height * 0.7f, 200, 140);
                    g.DrawPath(pen, path);
                }
                if (open)
                    g.DrawEllipse(pen, r.X + r.Width / 2 - r.Width * 0.11f, cy - r.Width * 0.11f, r.Width * 0.22f, r.Width * 0.22f);
                else
                    g.DrawLine(pen, r.X + r.Width * 0.15f, cy + r.Height * 0.30f, r.X + r.Width * 0.85f, cy - r.Height * 0.30f);
            }
        }
    }
}
