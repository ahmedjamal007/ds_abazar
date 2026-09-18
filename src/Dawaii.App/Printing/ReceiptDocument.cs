using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Dawaii.App.Printing
{
    public enum Align
    {
        Left,
        Center,
        Right,
    }

    /// <summary>Shared drawing state passed down the element tree.</summary>
    public sealed class RenderContext
    {
        public FontFamily Family;
        public CultureInfo Culture;
        public double BaseSize;
        public double PixelsPerDip = 1.0;
        public Brush Foreground = Brushes.Black;
        public Pen BorderPen;
        public double Gap = 4;

        public FormattedText Text(string text, double size, bool bold, double maxWidth, Align align)
        {
            var typeface = new Typeface(
                Family,
                FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            var ft = new FormattedText(
                text ?? string.Empty,
                Culture,
                FlowDirection.RightToLeft,
                typeface,
                size,
                Foreground,
                PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, maxWidth),
                Trimming = TextTrimming.None,
                TextAlignment = align == Align.Left ? TextAlignment.Left
                              : align == Align.Center ? TextAlignment.Center
                              : TextAlignment.Right,
            };
            return ft;
        }
    }

    public abstract class Element
    {
        public abstract double Measure(RenderContext ctx, double width);
        public abstract void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx);
    }

    internal static class Layout
    {
        public static double MeasureStack(IList<Element> els, RenderContext ctx, double width, double gap)
        {
            double h = 0;
            for (int i = 0; i < els.Count; i++)
            {
                h += els[i].Measure(ctx, width);
                if (i < els.Count - 1) h += gap;
            }
            return h;
        }

        public static void DrawStack(DrawingContext dc, IList<Element> els, double x, double y, double width, RenderContext ctx, double gap)
        {
            double cy = y;
            foreach (var el in els)
            {
                double eh = el.Measure(ctx, width);
                el.Draw(dc, x, cy, width, ctx);
                cy += eh + gap;
            }
        }

        /// <summary>Column widths for RTL columns; index 0 is the rightmost column.</summary>
        public static double[] ColumnWidths(double[] weights, double totalWidth)
        {
            double sum = 0;
            foreach (var w in weights) sum += w;
            var widths = new double[weights.Length];
            for (int i = 0; i < weights.Length; i++) widths[i] = totalWidth * weights[i] / sum;
            return widths;
        }
    }

    // ---- Leaf elements ----

    public sealed class TextElement : Element
    {
        public string Content;
        public Align Alignment = Align.Right;
        public bool Bold;
        public double Scale = 1.0;

        public TextElement() { }
        public TextElement(string content, Align align = Align.Right, bool bold = false, double scale = 1.0)
        {
            Content = content; Alignment = align; Bold = bold; Scale = scale;
        }

        public override double Measure(RenderContext ctx, double width)
        {
            return ctx.Text(Content, ctx.BaseSize * Scale, Bold, width, Alignment).Height;
        }

        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx)
        {
            dc.DrawText(ctx.Text(Content, ctx.BaseSize * Scale, Bold, width, Alignment), new Point(x, y));
        }
    }

    /// <summary>A row of cells laid out right-to-left (cells[0] is rightmost).</summary>
    public sealed class RowElement : Element
    {
        public string[] Cells;
        public double[] Weights;
        public Align[] Aligns;
        public bool Bold;
        public double Scale = 1.0;

        public override double Measure(RenderContext ctx, double width)
        {
            var widths = Layout.ColumnWidths(Weights, width);
            double max = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                var h = ctx.Text(Cells[i], ctx.BaseSize * Scale, Bold, widths[i], AlignOf(i)).Height;
                if (h > max) max = h;
            }
            return max;
        }

        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx)
        {
            var widths = Layout.ColumnWidths(Weights, width);
            double xRight = x + width;
            for (int i = 0; i < Cells.Length; i++)
            {
                double cw = widths[i];
                double cellX = xRight - cw;
                dc.DrawText(ctx.Text(Cells[i], ctx.BaseSize * Scale, Bold, cw, AlignOf(i)), new Point(cellX, y));
                xRight -= cw;
            }
        }

        private Align AlignOf(int i)
        {
            return Aligns != null && i < Aligns.Length ? Aligns[i] : Align.Right;
        }
    }

    public sealed class RuleElement : Element
    {
        public bool Dashed;
        public double Thickness = 1.2;

        public override double Measure(RenderContext ctx, double width)
        {
            return Thickness + 6;
        }

        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx)
        {
            var pen = new Pen(ctx.Foreground, Thickness);
            if (Dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
            pen.Freeze();
            double my = y + 3 + Thickness / 2;
            dc.DrawLine(pen, new Point(x, my), new Point(x + width, my));
        }
    }

    public sealed class SpaceElement : Element
    {
        public double Height = 8;
        public override double Measure(RenderContext ctx, double width) => Height;
        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx) { }
    }

    /// <summary>A bordered box wrapping a stack of child elements.</summary>
    public sealed class BoxElement : Element
    {
        public List<Element> Children = new List<Element>();
        public double Padding = 6;

        public override double Measure(RenderContext ctx, double width)
        {
            double inner = width - 2 * Padding;
            return Layout.MeasureStack(Children, ctx, inner, ctx.Gap) + 2 * Padding;
        }

        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx)
        {
            double h = Measure(ctx, width);
            var rect = new Rect(x + 0.5, y + 0.5, width - 1, h - 1);
            dc.DrawRectangle(null, ctx.BorderPen, rect);
            Layout.DrawStack(dc, Children, x + Padding, y + Padding, width - 2 * Padding, ctx, ctx.Gap);
        }
    }

    /// <summary>
    /// Bordered items table with a header row, an under-header rule, item rows,
    /// and vertical dividers between columns — the boxed table in the receipt.
    /// </summary>
    public sealed class TableElement : Element
    {
        public string[] Header;
        public List<string[]> Rows = new List<string[]>();
        public double[] Weights;
        public Align[] Aligns;
        public double Padding = 6;
        public double RowGap = 4;

        public override double Measure(RenderContext ctx, double width)
        {
            double inner = width - 2 * Padding;
            double h = Padding;
            h += RowHeight(ctx, Header, inner, true);
            h += 5; // under-header rule
            foreach (var row in Rows)
            {
                h += RowGap;
                h += RowHeight(ctx, row, inner, false);
            }
            h += Padding;
            return h;
        }

        public override void Draw(DrawingContext dc, double x, double y, double width, RenderContext ctx)
        {
            double total = Measure(ctx, width);
            var box = new Rect(x + 0.5, y + 0.5, width - 1, total - 1);
            dc.DrawRectangle(null, ctx.BorderPen, box);

            double innerX = x + Padding;
            double innerW = width - 2 * Padding;
            var widths = Layout.ColumnWidths(Weights, innerW);

            double cy = y + Padding;

            // Header (bold)
            DrawRow(dc, Header, innerX, cy, widths, ctx, true);
            double headerH = RowHeight(ctx, Header, innerW, true);
            cy += headerH + 2;

            // Under-header rule spanning inner width
            dc.DrawLine(ctx.BorderPen, new Point(innerX, cy), new Point(innerX + innerW, cy));
            cy += 3;

            // Item rows
            foreach (var row in Rows)
            {
                cy += RowGap;
                DrawRow(dc, row, innerX, cy, widths, ctx, false);
                cy += RowHeight(ctx, row, innerW, false);
            }

            // Vertical dividers between columns (RTL: start from right edge)
            double xRight = innerX + innerW;
            for (int i = 0; i < widths.Length - 1; i++)
            {
                xRight -= widths[i];
                dc.DrawLine(ctx.BorderPen, new Point(xRight, y + Padding), new Point(xRight, y + total - Padding));
            }
        }

        private double RowHeight(RenderContext ctx, string[] cells, double innerW, bool bold)
        {
            var widths = Layout.ColumnWidths(Weights, innerW);
            double max = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                var h = ctx.Text(cells[i], ctx.BaseSize, bold, widths[i], AlignOf(i)).Height;
                if (h > max) max = h;
            }
            return max;
        }

        private void DrawRow(DrawingContext dc, string[] cells, double innerX, double y, double[] widths, RenderContext ctx, bool bold)
        {
            double xRight = innerX + Sum(widths);
            for (int i = 0; i < cells.Length; i++)
            {
                double cw = widths[i];
                double cellX = xRight - cw;
                // small inner padding so text does not touch the divider lines
                dc.DrawText(ctx.Text(cells[i], ctx.BaseSize, bold, cw - 6, AlignOf(i)), new Point(cellX + 3, y));
                xRight -= cw;
            }
        }

        private static double Sum(double[] a)
        {
            double s = 0; foreach (var v in a) s += v; return s;
        }

        private Align AlignOf(int i)
        {
            return Aligns != null && i < Aligns.Length ? Aligns[i] : Align.Right;
        }
    }

    /// <summary>Top-level receipt: an ordered list of elements.</summary>
    public sealed class ReceiptDocument
    {
        public List<Element> Elements { get; } = new List<Element>();

        public ReceiptDocument Add(Element el)
        {
            Elements.Add(el);
            return this;
        }
    }
}
