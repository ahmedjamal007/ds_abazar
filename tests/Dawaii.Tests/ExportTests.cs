using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dawaii.App.Ui;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Real-output smoke tests for the multi-section report exporter (V1.2 shift report):
    /// each format writes an actual temp file and its signature/content is validated.
    /// </summary>
    [TestFixture]
    public class ExportTests
    {
        private static List<ReportSection> SampleSections() => new List<ReportSection>
        {
            new ReportSection("ملخص المبيعات", new[] { "البيان", "القيمة" }, new List<string[]>
            {
                new[] { "عدد الفواتير", "12" },
                new[] { "إجمالي المبيعات", "4500.00" },
                new[] { "النقد المتوقع في الدرج", "4100.00" }
            }),
            new ReportSection("حضور ومبيعات الموظفين",
                new[] { "الموظف", "الحضور", "الفواتير", "المبيعات", "نقدي", "أدوية", "إجمالي المصروف" },
                new List<string[]>
                {
                    new[] { "سارة", "08:15", "7", "2500.00", "100.00", "20.00", "120.00" },
                    new[] { "علي", "14:02", "5", "2000.00", "0.00", "0.00", "0.00" }
                }),
            new ReportSection("الأصناف المباعة", new[] { "الصنف", "الكمية (حبة)", "الإيراد" }, new List<string[]>
            {
                new[] { "بنادول", "120", "240.00" },
                new[] { "بروفين", "50", "120.00" }
            })
        };

        private static string TempPath(string ext)
            => Path.Combine(Path.GetTempPath(), "dawaii_test_" + Guid.NewGuid().ToString("N") + ext);

        [Test]
        public void Xlsx_MultiSection_WritesValidZip()
        {
            string path = TempPath(".xlsx");
            try
            {
                ReportExporter.XlsxSections(path, "تقرير الوردية", SampleSections());
                byte[] head = File.ReadAllBytes(path);
                Assert.That(head.Length, Is.GreaterThan(1000));
                Assert.That(head[0], Is.EqualTo(0x50)); // 'P'
                Assert.That(head[1], Is.EqualTo(0x4B)); // 'K' — xlsx is a zip
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Pdf_MultiSection_WritesValidPdf()
        {
            string path = TempPath(".pdf");
            try
            {
                ReportExporter.PdfSections(path, "تقرير الوردية", SampleSections());

                byte[] raw = File.ReadAllBytes(path);
                Assert.That(raw.Length, Is.GreaterThan(1000));
                Assert.That(Encoding.ASCII.GetString(raw, 0, 5), Is.EqualTo("%PDF-"));

                // A file can start with %PDF- and still be unopenable, or open and hold nothing. The
                // check that means something is reading it back: this is the only test standing
                // between a PDF library swap and a pharmacist emailing an empty report to an
                // accountant. Asserting on the first four bytes would have passed either way.
                Assert.That(Encoding.ASCII.GetString(raw, raw.Length - 6, 5), Is.EqualTo("%%EOF"),
                    "a truncated PDF still begins with %PDF-");

                using (PdfSharp.Pdf.PdfDocument reopened =
                       PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                {
                    Assert.That(reopened.PageCount, Is.GreaterThanOrEqualTo(1),
                        "the report has content, so the PDF must have pages to put it on");
                    Assert.That(reopened.Info.Title, Is.EqualTo("تقرير الوردية"),
                        "and the Arabic title survives the round trip through the writer");

                    // A4 is 595.28 x 841.89 POINTS. The page used to be declared 827 x 1169 points —
                    // those numbers are the bitmap's PIXELS, A4 at 100 DPI — making every page 11.5
                    // inches wide. The aspect ratio matched, so it looked right and scaled to fit
                    // when printed; a printer set to "actual size" would have cropped it.
                    PdfSharp.Pdf.PdfPage first = reopened.Pages[0];
                    Assert.That(first.Width.Point, Is.EqualTo(595.28).Within(1.0), "A4 width in points");
                    Assert.That(first.Height.Point, Is.EqualTo(841.89).Within(1.0), "A4 height in points");
                }
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Csv_MultiSection_HasBomSectionsAndEscaping()
        {
            string path = TempPath(".csv");
            try
            {
                var sections = SampleSections();
                sections[0].Rows.Add(new[] { "قيمة، بفاصلة", "te\"st" }); // needs quoting
                ReportExporter.CsvSections(path, "تقرير الوردية", sections);

                byte[] raw = File.ReadAllBytes(path);
                Assert.That(raw[0], Is.EqualTo(0xEF)); // UTF-8 BOM so Excel shows Arabic correctly
                Assert.That(raw[1], Is.EqualTo(0xBB));
                Assert.That(raw[2], Is.EqualTo(0xBF));

                string text = File.ReadAllText(path);
                Assert.That(text, Does.Contain("تقرير الوردية"));
                Assert.That(text, Does.Contain("حضور ومبيعات الموظفين"));
                Assert.That(text, Does.Contain("الأصناف المباعة"));
                Assert.That(text, Does.Contain("سارة,08:15,7,2500.00,100.00,20.00,120.00"));
                Assert.That(text, Does.Contain("\"te\"\"st\""), "Quotes must be escaped.");
            }
            finally { File.Delete(path); }
        }
    }
}
