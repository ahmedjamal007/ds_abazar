using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>One titled table inside a printed/exported report.</summary>
    public class ReportSection
    {
        public string Title;
        public string[] Headers;
        public IList<string[]> Rows;
        public ReportSection(string title, string[] headers, IList<string[]> rows)
        { Title = title; Headers = headers; Rows = rows; }
    }

    /// <summary>
    /// Prints RTL tables via GDI (which shapes Arabic correctly). Supports multi-section reports —
    /// e.g. the shift report: sales summary + per-employee table + items sold (V1.2).
    /// </summary>
    public static class ReportPrinter
    {
        public static void Print(IWin32Window owner, string title, string[] headers, IList<string[]> rows)
            => PrintSections(owner, title, new List<ReportSection> { new ReportSection(null, headers, rows) });

        public static void PrintSections(IWin32Window owner, string title, IList<ReportSection> sections)
        {
            using (var doc = new PrintDocument())
            {
                int sectionIndex = 0, rowIndex = 0;
                bool firstPage = true;
                doc.DocumentName = title;
                doc.PrintPage += (s, e) =>
                {
                    var g = e.Graphics;
                    Rectangle area = e.MarginBounds;
                    float y = area.Top;

                    using (var titleFont = new Font(Theme.FontFamily, 16, FontStyle.Bold))
                    using (var secFont = new Font(Theme.FontFamily, 12, FontStyle.Bold))
                    using (var headFont = new Font(Theme.FontFamily, 10, FontStyle.Bold))
                    using (var rowFont = new Font(Theme.FontFamily, 10))
                    {
                        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft);

                        if (firstPage)
                        {
                            g.DrawString(title, titleFont, Brushes.Black, new RectangleF(area.Left, y, area.Width, 30), rtl);
                            y += 34;
                            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), rowFont, Brushes.Gray,
                                new RectangleF(area.Left, y, area.Width, 20), rtl);
                            y += 28;
                            firstPage = false;
                        }

                        while (sectionIndex < sections.Count)
                        {
                            ReportSection sec = sections[sectionIndex];

                            if (rowIndex == 0)
                            {
                                if (y > area.Bottom - 90) { e.HasMorePages = true; return; } // section start would not fit
                                if (!string.IsNullOrEmpty(sec.Title))
                                {
                                    g.DrawString(sec.Title, secFont, Brushes.Black, new RectangleF(area.Left, y, area.Width, 24), rtl);
                                    y += 28;
                                }
                                float cw0 = (float)area.Width / sec.Headers.Length;
                                for (int c = 0; c < sec.Headers.Length; c++)
                                {
                                    float x = area.Right - (c + 1) * cw0;
                                    g.DrawString(sec.Headers[c], headFont, Brushes.Black, new RectangleF(x, y, cw0 - 4, 20), rtl);
                                }
                                y += 24;
                                g.DrawLine(Pens.Black, area.Left, y - 4, area.Right, y - 4);
                            }

                            float colWidth = (float)area.Width / sec.Headers.Length;
                            while (rowIndex < sec.Rows.Count)
                            {
                                if (y > area.Bottom - 24) { e.HasMorePages = true; return; }
                                string[] row = sec.Rows[rowIndex];
                                for (int c = 0; c < sec.Headers.Length && c < row.Length; c++)
                                {
                                    float x = area.Right - (c + 1) * colWidth;
                                    g.DrawString(row[c] ?? "", rowFont, Brushes.Black, new RectangleF(x, y, colWidth - 4, 20), rtl);
                                }
                                y += 22;
                                rowIndex++;
                            }

                            sectionIndex++;
                            rowIndex = 0;
                            y += 18; // gap between sections
                        }
                        e.HasMorePages = false;
                    }
                };

                using (var dlg = new PrintDialog { Document = doc, UseEXDialog = true })
                    if (dlg.ShowDialog(owner) == DialogResult.OK)
                        doc.Print();
            }
        }
    }
}
