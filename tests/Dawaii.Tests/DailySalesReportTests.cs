using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.App.Printing;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The end-of-shift slip (V2.3 "تقرير مبيعات يومي") and the one list of payment methods it — and
    /// every other screen — now reads from.
    ///
    /// The slip is checked in two ways: the figures, which have to reconcile with themselves (the
    /// per-method table adds up to the net total, credit is its own row, returns come off), and the
    /// rendering, which has to come out at the head's width with every label present. The rendering
    /// test also writes the PNG to disk so a person can look at it — a receipt is judged by eye.
    /// </summary>
    [TestFixture]
    public class DailySalesReportTests
    {
        // ---------------- the one list of payment methods ----------------

        [TestCase("Cash", "Cash")]
        [TestCase("cash", "Cash")]
        [TestCase(null, "Cash")]
        [TestCase("", "Cash")]
        [TestCase("Bankak", "Bankak")]
        [TestCase("Fawry", "Fawry")]
        [TestCase("Ocash", "Ocash")]
        [TestCase("اوكاش", "Ocash")]     // the rows the POS wrote with the label as the code
        [TestCase("أوكاش", "Ocash")]
        [TestCase("nonsense", "Cash")]
        public void Normalize_ReadsEverySpellingASaleRowMightHold(string stored, string expected)
        {
            Assert.That(PaymentMethods.Normalize(stored), Is.EqualTo(expected));
        }

        [Test]
        public void LabelAr_KnowsOcash_WhichTheFourOldCopiesDidNot()
        {
            Assert.That(PaymentMethods.LabelAr("Ocash"), Is.EqualTo("أوكاش"));
            Assert.That(PaymentMethods.LabelAr("اوكاش"), Is.EqualTo("أوكاش"), "a legacy row prints as what it was");
            Assert.That(PaymentMethods.LabelAr("Bankak"), Is.EqualTo("بنكك"));
            Assert.That(PaymentMethods.LabelAr(null), Is.EqualTo("كاش"));
        }

        [Test]
        public void ThePosOffersEveryMethod_InTheListsOrder_WithTheCodeAsTheValue()
        {
            // The POS builds its dropdown from PaymentMethods.All, so a method added there reaches the
            // till, the receipt and the shift report together; and the stored value is the CODE.
            Assert.That(PaymentMethods.All.Select(m => m.Code), Is.EqualTo(new[] { "Cash", "Bankak", "Fawry", "Ocash" }));
            Assert.That(PaymentMethods.All.All(m => m.Code.All(ch => ch < 128)), "codes are ASCII; labels are Arabic");
        }

        // ---------------- the figures ----------------

        private static Sale Sale(int number, decimal total, string method, SaleType type = SaleType.Cash,
            decimal returned = 0m, SaleStatus status = SaleStatus.Completed)
            => new Sale
            {
                Id = number, SaleNumber = number, Total = total, Subtotal = total, PaymentMethod = method,
                SaleType = type, ReturnedTotal = returned, Status = status, CreatedAt = DateTime.Today.AddHours(10)
            };

        private static EmployeeDaySheet Sheet(params Sale[] sales) => new EmployeeDaySheet
        {
            Sales = sales.ToList(),
            FirstLoginAt = DateTime.Today.AddHours(8).AddMinutes(30)
        };

        [Test]
        public void ByPaymentMethod_CountsAndSumsEachWayOfPaying()
        {
            EmployeeDaySheet sheet = Sheet(
                Sale(1, 1000m, "Cash"), Sale(2, 500m, "Cash"),
                Sale(3, 2000m, "Bankak"),
                Sale(4, 750m, "Fawry"),
                Sale(5, 3000m, "Ocash"), Sale(6, 250m, "اوكاش"),     // one new-style row, one legacy
                Sale(7, 4000m, null, SaleType.Credit));

            var rows = sheet.ByPaymentMethod.ToDictionary(r => r.Code);

            Assert.That(rows["Cash"].Count, Is.EqualTo(2));   Assert.That(rows["Cash"].Total, Is.EqualTo(1500m));
            Assert.That(rows["Bankak"].Count, Is.EqualTo(1)); Assert.That(rows["Bankak"].Total, Is.EqualTo(2000m));
            Assert.That(rows["Fawry"].Count, Is.EqualTo(1));  Assert.That(rows["Fawry"].Total, Is.EqualTo(750m));
            Assert.That(rows["Ocash"].Count, Is.EqualTo(2), "the legacy spelling lands in the same bucket");
            Assert.That(rows["Ocash"].Total, Is.EqualTo(3250m));
            Assert.That(rows["Credit"].Count, Is.EqualTo(1)); Assert.That(rows["Credit"].Total, Is.EqualTo(4000m));
        }

        [Test]
        public void ByPaymentMethod_AlwaysListsEveryMethod_EvenAtZero()
        {
            EmployeeDaySheet sheet = Sheet(Sale(1, 1000m, "Cash"));
            var rows = sheet.ByPaymentMethod;

            Assert.That(rows.Select(r => r.Code), Is.EqualTo(new[] { "Cash", "Bankak", "Fawry", "Ocash", "Credit" }),
                "a printed table with a missing line is read as a missing sale, so the shape never changes");
            Assert.That(rows.Where(r => r.Code != "Cash").All(r => r.Count == 0 && r.Total == 0m));
        }

        [Test]
        public void ByPaymentMethod_AddsUpToTheNetTotal_WithReturnsTakenOff()
        {
            // A 1,000 cash sale with 300 returned, and a 2,000 Bankak sale returned whole.
            EmployeeDaySheet sheet = Sheet(
                Sale(1, 1000m, "Cash", returned: 300m, status: SaleStatus.PartiallyReturned),
                Sale(2, 2000m, "Bankak", status: SaleStatus.Returned),
                Sale(3, 500m, "Fawry"));

            Assert.That(sheet.SalesTotal, Is.EqualTo(3500m), "gross, what the screen has always shown");
            Assert.That(sheet.ReturnedTotal, Is.EqualTo(2300m));
            Assert.That(sheet.NetTotal, Is.EqualTo(1200m));
            Assert.That(sheet.ByPaymentMethod.Sum(r => r.Total), Is.EqualTo(sheet.NetTotal),
                "the table reconciles with the total printed under it");
            Assert.That(sheet.ByPaymentMethod.Single(r => r.Code == "Cash").Total, Is.EqualTo(700m));
            Assert.That(sheet.ByPaymentMethod.Single(r => r.Code == "Bankak").Total, Is.Zero);
        }

        // ---------------- the slip ----------------

        private static ReceiptInfo Info() => new ReceiptInfo { PharmacyName = "صيدلية الاختبار", Currency = "ج.س", Width = 48 };
        private static User Sara() => new User { Id = 7, Username = "sara", FullName = "سارة أحمد", Role = Role.Cashier, IsActive = true };

        /// <summary>Every piece of text on the slip, in drawing order, so the assertions read like the paper.</summary>
        private static List<string> Texts(ReceiptDocument doc)
        {
            var all = new List<string>();
            void Walk(Element e)
            {
                switch (e)
                {
                    case TextElement t: all.Add(t.Content); break;
                    case RowElement r: all.AddRange(r.Cells); break;
                    case TableElement tb: all.AddRange(tb.Header); foreach (var row in tb.Rows) all.AddRange(row); break;
                    case BoxElement b: foreach (var c in b.Children) Walk(c); break;
                }
            }
            foreach (Element e in doc.Elements) Walk(e);
            return all;
        }

        [Test]
        public void Slip_CarriesEveryFigureTheManagerAsksFor()
        {
            EmployeeDaySheet sheet = Sheet(
                Sale(1, 1000m, "Cash"), Sale(2, 2000m, "Bankak"), Sale(3, 750m, "Fawry"), Sale(4, 3000m, "Ocash"));

            List<string> texts = Texts(DailySalesReceiptBuilder.Build(sheet, Sara(), DateTime.Today, DateTime.Today.AddDays(1), Info()));

            Assert.That(texts, Does.Contain("تقرير مبيعات يومي"));
            Assert.That(texts, Does.Contain("الوردية:").And.Contain("سارة أحمد"), "who was on");
            Assert.That(texts, Does.Contain("الحضور:").And.Contain("08:30"), "when they clocked in");
            Assert.That(texts, Does.Contain("عدد الفواتير:").And.Contain("4"));
            Assert.That(texts, Does.Contain("إجمالي المبيعات:").And.Contain("6,750.00 ج.س"));

            // The table: header, then one line per method with its count and amount.
            Assert.That(texts, Does.Contain("طريقة الدفع").And.Contain("عدد الفواتير").And.Contain("الإجمالي"));
            foreach (string label in new[] { "كاش", "بنكك", "فوري", "أوكاش", "آجل" })
                Assert.That(texts, Does.Contain(label), label + " row missing");
            Assert.That(texts, Does.Contain("3,000.00"), "the أوكاش amount");
        }

        [Test]
        public void Slip_SaysWhenNobodyClockedIn()
        {
            EmployeeDaySheet sheet = Sheet(Sale(1, 100m, "Cash"));
            sheet.FirstLoginAt = null;
            List<string> texts = Texts(DailySalesReceiptBuilder.Build(sheet, Sara(), DateTime.Today, DateTime.Today.AddDays(1), Info()));
            Assert.That(texts, Does.Contain("لم يُسجَّل"));
        }

        [Test]
        public void Slip_ShowsReturnsAndTheNet_OnlyWhenThereWereReturns()
        {
            EmployeeDaySheet clean = Sheet(Sale(1, 1000m, "Cash"));
            EmployeeDaySheet withReturn = Sheet(Sale(1, 1000m, "Cash", returned: 250m, status: SaleStatus.PartiallyReturned));

            Assert.That(Texts(DailySalesReceiptBuilder.Build(clean, Sara(), DateTime.Today, DateTime.Today.AddDays(1), Info())),
                Does.Not.Contain("المرتجعات:"), "no returns, no line — nothing to explain");
            List<string> texts = Texts(DailySalesReceiptBuilder.Build(withReturn, Sara(), DateTime.Today, DateTime.Today.AddDays(1), Info()));
            Assert.That(texts, Does.Contain("المرتجعات:").And.Contain("الصافي:"));
            Assert.That(texts, Does.Contain("750.00 ج.س"), "the net after the return");
        }

        [Test]
        public void Slip_NamesTheRange_WhenItIsMoreThanOneDay()
        {
            EmployeeDaySheet sheet = Sheet(Sale(1, 100m, "Cash"));
            var from = new DateTime(2026, 9, 1);
            List<string> texts = Texts(DailySalesReceiptBuilder.Build(sheet, Sara(), from, from.AddDays(7), Info()));
            Assert.That(texts.Any(t => t.Contains("2026-09-01 → 2026-09-07")), "the range, inside its LRM marks");
        }

        [Test]
        [Apartment(System.Threading.ApartmentState.STA)]
        public void Slip_RendersAtTheHeadsWidth_AndIsWrittenOutForAHumanToLookAt()
        {
            EmployeeDaySheet sheet = Sheet(
                Sale(1, 1000m, "Cash"), Sale(2, 500m, "Cash"),
                Sale(3, 2000m, "Bankak"),
                Sale(4, 750m, "Fawry"),
                Sale(5, 3000m, "Ocash"),
                Sale(6, 4000m, null, SaleType.Credit),
                Sale(7, 600m, "Cash", returned: 200m, status: SaleStatus.PartiallyReturned));
            sheet.Expenses = new List<EmployeeExpense> { new EmployeeExpense { Amount = 150m, Type = ExpenseType.Money } };

            ReceiptDocument doc = DailySalesReceiptBuilder.Build(sheet, Sara(), DateTime.Today, DateTime.Today.AddDays(1), Info());
            var rendered = ReceiptImageRenderer.Render(doc, ReceiptImageRenderer.Width80mm);

            Assert.That(rendered.Width, Is.EqualTo(576), "an 80mm head prints 576 dots — same as a receipt");
            Assert.That(rendered.Height, Is.GreaterThan(300), "the slip rendered empty");

            string outDir = Environment.GetEnvironmentVariable("DAWAII_RENDER_DIR");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, "daily_sales_summary.png"), rendered.ToPng());
            }
        }

        // ---------------- the migration ----------------

        [Test]
        public void Migration_RewritesTheRowsThePosStoredWithTheLabelAsTheCode()
        {
            string path = Path.Combine(Path.GetTempPath(), "dawaii_ocash_" + Guid.NewGuid().ToString("N") + ".db");
            var db = new SqliteConnectionFactory(path);
            try
            {
                var init = new DatabaseInitializer(db);
                init.ApplySchemaAndSeed();
                init.EnsureDefaultAdmin();
                User admin = new SqliteUserRepository(db).GetByUsername("admin");

                using (var conn = db.OpenConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO sales (user_id, payment_method, total) VALUES (" + admin.Id + ", 'اوكاش', 100)";
                    cmd.ExecuteNonQuery();
                }

                // The next start-up runs the migrations again — idempotent, and this one rewrites the row.
                new DatabaseInitializer(db).ApplySchemaAndSeed();

                using (var conn = db.OpenConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT payment_method FROM sales";
                    Assert.That(cmd.ExecuteScalar(), Is.EqualTo("Ocash"));
                }
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                GC.Collect(); GC.WaitForPendingFinalizers();
                foreach (string f in new[] { path, path + "-wal", path + "-shm" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }
}
