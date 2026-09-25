using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Dawaii.Core.Printing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Dawaii.App.Ui
{
    public enum ExportFormat { Pdf, Xlsx, Csv }

    /// <summary>
    /// Exports report tables to .xlsx (NPOI), .pdf (GDI-rendered pages so Arabic shapes correctly)
    /// and .csv (UTF-8 with BOM so Excel opens Arabic correctly). Supports multi-section reports
    /// (V1.2 shift report: summary + employees + items). Fully offline.
    /// </summary>
    public static class ReportExporter
    {
        // ---------------- single-table convenience (existing callers) ----------------

        public static string SaveWithDialog(IWin32Window owner, string title, string[] headers, IList<string[]> rows, bool pdf)
            => SaveSectionsWithDialog(owner, title,
                new List<ReportSection> { new ReportSection(null, headers, rows) },
                pdf ? ExportFormat.Pdf : ExportFormat.Xlsx);

        public static void ToXlsx(string path, string title, string[] headers, IList<string[]> rows)
            => XlsxSections(path, title, new List<ReportSection> { new ReportSection(null, headers, rows) });

        public static void ToPdf(string path, string title, string[] headers, IList<string[]> rows)
            => PdfSections(path, title, new List<ReportSection> { new ReportSection(null, headers, rows) });

        // ---------------- multi-section API ----------------

        /// <summary>Asks for a location then writes the chosen format. Returns the saved path or null.</summary>
        public static string SaveSectionsWithDialog(IWin32Window owner, string title, IList<ReportSection> sections, ExportFormat format)
        {
            string ext = format == ExportFormat.Pdf ? ".pdf" : format == ExportFormat.Xlsx ? ".xlsx" : ".csv";
            string filter = format == ExportFormat.Pdf ? "PDF (*.pdf)|*.pdf"
                          : format == ExportFormat.Xlsx ? "Excel (*.xlsx)|*.xlsx"
                          : "CSV (*.csv)|*.csv";
            using (var dlg = new SaveFileDialog { Title = "حفظ التقرير", FileName = SafeFileName(title) + ext, Filter = filter })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                switch (format)
                {
                    case ExportFormat.Pdf: PdfSections(dlg.FileName, title, sections); break;
                    case ExportFormat.Xlsx: XlsxSections(dlg.FileName, title, sections); break;
                    default: CsvSections(dlg.FileName, title, sections); break;
                }
                return dlg.FileName;
            }
        }

        // ---------------- CSV ----------------

        public static void CsvSections(string path, string title, IList<ReportSection> sections)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Csv(title));
            sb.AppendLine(Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            foreach (ReportSection sec in sections)
            {
                sb.AppendLine();
                if (!string.IsNullOrEmpty(sec.Title)) sb.AppendLine(Csv(sec.Title));
                sb.AppendLine(string.Join(",", Array.ConvertAll(sec.Headers, Csv)));
                foreach (string[] row in sec.Rows)
                    sb.AppendLine(string.Join(",", Array.ConvertAll(row, Csv)));
            }
            // UTF-8 with BOM so Excel detects the encoding and Arabic displays correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // ---------------- Excel ----------------

        public static void XlsxSections(string path, string title, IList<ReportSection> sections)
        {
            var wb = new XSSFWorkbook();
            ISheet sheet = wb.CreateSheet("تقرير");
            sheet.IsRightToLeft = true;

            var boldFont = wb.CreateFont();
            boldFont.IsBold = true;
            var boldStyle = wb.CreateCellStyle();
            boldStyle.SetFont(boldFont);

            int rowIdx = 0;
            var titleRow = sheet.CreateRow(rowIdx++);
            var tCell = titleRow.CreateCell(0);
            tCell.SetCellValue(title);
            tCell.CellStyle = boldStyle;
            sheet.CreateRow(rowIdx++).CreateCell(0).SetCellValue(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            int maxCols = 1;
            foreach (ReportSection sec in sections)
            {
                rowIdx++; // blank row between sections
                if (!string.IsNullOrEmpty(sec.Title))
                {
                    var sr = sheet.CreateRow(rowIdx++);
                    var sc = sr.CreateCell(0);
                    sc.SetCellValue(sec.Title);
                    sc.CellStyle = boldStyle;
                }

                IRow head = sheet.CreateRow(rowIdx++);
                for (int c = 0; c < sec.Headers.Length; c++)
                {
                    var cell = head.CreateCell(c);
                    cell.SetCellValue(sec.Headers[c]);
                    cell.CellStyle = boldStyle;
                }
                maxCols = Math.Max(maxCols, sec.Headers.Length);

                foreach (string[] row in sec.Rows)
                {
                    IRow r = sheet.CreateRow(rowIdx++);
                    for (int c = 0; c < row.Length; c++)
                    {
                        if (decimal.TryParse(row[c], out decimal num))
                            r.CreateCell(c).SetCellValue((double)num);   // numbers stay numeric for SUM
                        else
                            r.CreateCell(c).SetCellValue(row[c] ?? "");
                    }
                }
            }

            for (int c = 0; c < maxCols; c++) sheet.AutoSizeColumn(c);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                wb.Write(fs);
        }

        // ---------------- PDF ----------------

        private const int PageW = 827, PageH = 1169, Margin = 40; // A4 at ~100 DPI

        public static void PdfSections(string path, string title, IList<ReportSection> sections)
        {
            // PdfSharp encodes WinAnsi strings with CP1252 while saving any document at all — even a
            // PDF of nothing but images, because the metadata dates go through it. Modern .NET does
            // not carry the legacy code pages, so without this the save throws NotSupportedException
            // from deep inside the library and the export dies at the last step.
            LegacyEncodings.Register();

            var doc = new PdfDocument();
            doc.Info.Title = title;

            foreach (Bitmap bmp in RenderPages(title, sections))
                using (bmp)
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    PdfPage page = doc.AddPage();

                    // A4, declared in the unit PDF actually uses. PageW/PageH are PIXELS for the
                    // bitmap above — 827x1169 is A4 at 100 DPI — and assigning them straight to
                    // page.Width made every page 827 POINTS wide, which is 11.5 inches, not 8.27.
                    // The aspect ratio matches so it looked right and scaled to fit when printed,
                    // but a printer set to "actual size" would have cropped it. PDFsharp 6 marked
                    // that implicit conversion obsolete for exactly this misreading.
                    page.Size = PdfSharp.PageSize.A4;

                    using (XGraphics g = XGraphics.FromPdfPage(page))
                    using (XImage img = XImage.FromStream(ms))
                        g.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                }
            doc.Save(path);
        }

        /// <summary>Renders the sections into A4 bitmaps with the same flow logic as the printer.</summary>
        private static IEnumerable<Bitmap> RenderPages(string title, IList<ReportSection> sections)
        {
            float width = PageW - Margin * 2;
            var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft);
            int sectionIndex = 0, rowIndex = 0;
            bool firstPage = true;

            using (var titleFont = new Font(Theme.FontFamily, 18, FontStyle.Bold))
            using (var secFont = new Font(Theme.FontFamily, 12.5f, FontStyle.Bold))
            using (var headFont = new Font(Theme.FontFamily, 11, FontStyle.Bold))
            using (var rowFont = new Font(Theme.FontFamily, 10.5f))
            {
                while (sectionIndex < sections.Count)
                {
                    var bmp = new Bitmap(PageW, PageH);
                    bool pageFull = false;
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        float y = Margin;

                        if (firstPage)
                        {
                            g.DrawString(title, titleFont, Brushes.Black, new RectangleF(Margin, y, width, 34), rtl);
                            y += 38;
                            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), rowFont, Brushes.Gray,
                                new RectangleF(Margin, y, width, 20), rtl);
                            y += 30;
                            firstPage = false;
                        }

                        while (sectionIndex < sections.Count && !pageFull)
                        {
                            ReportSection sec = sections[sectionIndex];
                            float colWidth = width / sec.Headers.Length;

                            if (rowIndex == 0)
                            {
                                if (y > PageH - Margin - 100) { pageFull = true; break; }
                                if (!string.IsNullOrEmpty(sec.Title))
                                {
                                    g.DrawString(sec.Title, secFont, Brushes.Black, new RectangleF(Margin, y, width, 24), rtl);
                                    y += 30;
                                }
                                for (int c = 0; c < sec.Headers.Length; c++)
                                {
                                    float x = PageW - Margin - (c + 1) * colWidth;
                                    g.DrawString(sec.Headers[c], headFont, Brushes.Black, new RectangleF(x, y, colWidth - 4, 22), rtl);
                                }
                                y += 26;
                                g.DrawLine(Pens.Black, Margin, y - 4, PageW - Margin, y - 4);
                            }

                            while (rowIndex < sec.Rows.Count)
                            {
                                if (y > PageH - Margin - 26) { pageFull = true; break; }
                                string[] row = sec.Rows[rowIndex];
                                for (int c = 0; c < sec.Headers.Length && c < row.Length; c++)
                                {
                                    float x = PageW - Margin - (c + 1) * colWidth;
                                    g.DrawString(row[c] ?? "", rowFont, Brushes.Black, new RectangleF(x, y, colWidth - 4, 22), rtl);
                                }
                                y += 24;
                                rowIndex++;
                            }

                            if (!pageFull)
                            {
                                sectionIndex++;
                                rowIndex = 0;
                                y += 20;
                            }
                        }
                    }
                    yield return bmp;
                }
            }
        }

        private static string SafeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
