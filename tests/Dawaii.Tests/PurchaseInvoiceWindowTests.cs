using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Dawaii.App;
using Dawaii.App.Forms;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The delivery window standing beside the app instead of on top of it (V2.3.2).
    ///
    /// A delivery used to be typed in a modal window, so a customer arriving mid-invoice left the
    /// pharmacist with a program that ignored every click. Adding a minimize button alone would not
    /// have fixed that: a modal window keeps the main window disabled underneath it whether it is
    /// minimized or not. So the window is modeless now, and these tests cover what that opens up —
    /// one window at a time, owned by the main window rather than by a dialog that is about to close,
    /// and shut cleanly at logout.
    ///
    /// The refusal message itself is not tested. It is a message box, and a message box in a test is
    /// a hang — which is why the rule lives in TryPresent and only the wording lives above it.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class PurchaseInvoiceWindowTests
    {
        private string _path;
        private Supplier _supplier;
        private int _panadol;
        private Form _main;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_invwin_" + Guid.NewGuid().ToString("N") + ".db");
            var services = new AppServices(new SqliteConnectionFactory(_path));
            services.Initializer.ApplySchemaAndSeed();
            services.Initializer.EnsureDefaultAdmin();
            Session.Services = services;
            Session.CurrentUser = services.Users.GetByUsername("admin");

            _supplier = services.Suppliers.Get(services.Suppliers.CreateSupplier(Session.CurrentUser, "شركة النيل"));
            _panadol = services.Items.Add(new Item
            {
                NameEn = "zz-panadol", UnitsPerStrip = 10, StripsPerBox = 10, IsActive = true
            });

            _main = new Form();     // stands in for MainForm
            _main.CreateControl();

            PurchaseDrafts.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            CloseOpen();
            _main?.Dispose();
            PurchaseDrafts.Clear();
            Session.CurrentUser = null;
            Session.Services = null;
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // ---------------- reaching past the message box ----------------

        private static readonly BindingFlags Any =
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>Opens a delivery the way the buttons do, minus the box that would hang the run.</summary>
        private static bool TryOpen(Supplier supplier, Form owner, Action onClosed = null)
        {
            MethodInfo m = typeof(PurchaseInvoiceForm).GetMethod("TryPresent", Any);
            var build = new Func<PurchaseInvoiceForm>(() => new PurchaseInvoiceForm(supplier));
            try { return (bool)m.Invoke(null, new object[] { build, owner, onClosed }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private static PurchaseInvoiceForm Open()
            => (PurchaseInvoiceForm)typeof(PurchaseInvoiceForm)
                .GetField("_open", Any).GetValue(null);

        private static bool IsOpen => PurchaseInvoiceForm.IsOpen;

        private static void CloseOpen() => PurchaseInvoiceForm.CloseOpen();

        private static void Type(PurchaseInvoiceForm form, params PurchaseInvoiceLine[] lines)
        {
            var list = (List<PurchaseInvoiceLine>)typeof(PurchaseInvoiceForm)
                .GetField("_lines", Any).GetValue(form);
            list.AddRange(lines);
        }

        private PurchaseInvoiceLine Line(int boxes)
            => new PurchaseInvoiceLine
            {
                ItemId = _panadol, ItemName = "zz-panadol", QuantityBoxes = boxes, StripsPerBox = 10,
                BoxPurchasePrice = 1000m, BoxSellingPrice = 1400m,
                ExpiryDate = DateTime.Today.AddYears(1), BatchNumber = "LOT-1"
            };

        // ---------------- the window ----------------

        [Test]
        public void ADeliveryBeingTyped_CanBeMinimized()
        {
            using (var form = new PurchaseInvoiceForm(_supplier))
                Assert.That(form.MinimizeBox, Is.True,
                    "this is the whole point — put it aside, serve the customer, come back to it");
        }

        [Test]
        public void ACorrection_CannotBeMinimized_BecauseItIsStillShownOverItsListing()
        {
            using (var form = new PurchaseInvoiceForm(Filed().Id))
                Assert.That(form.MinimizeBox, Is.False);
        }

        [Test]
        public void ACorrection_OpensOnTheCompanyTheInvoiceWasFiledAgainst()
        {
            // Regression: the company list was bound with DataSource, which does nothing until the
            // control has a binding context — so the combo was empty, picking the invoice's own
            // company threw, and every correction ended as "تعذّر فتح الفاتورة" instead of a screen.
            int otherId = Session.Services.Suppliers.CreateSupplier(Session.CurrentUser, "شركة أخرى");
            PurchaseInvoice filed = Filed();

            using (var form = new PurchaseInvoiceForm(filed.Id))
            {
                var combo = (ComboBox)typeof(PurchaseInvoiceForm).GetField("_company", Any).GetValue(form);

                Assert.That(combo.Items.Count, Is.EqualTo(2), "both companies are offered");
                Assert.That(((Supplier)combo.SelectedItem).Id, Is.EqualTo(_supplier.Id),
                    "and it opens on the one the delivery actually came from");
                Assert.That(((Supplier)combo.SelectedItem).Id, Is.Not.EqualTo(otherId));
            }
        }

        private PurchaseInvoice Filed()
            => Session.Services.Suppliers.RecordInvoice(
                Session.CurrentUser, _supplier.Id, "علي", "INV-1", DateTime.Today, new[] { Line(2) });

        [Test]
        public void OpeningADelivery_LeavesItOpenUntilItIsClosed()
        {
            Assert.That(IsOpen, Is.False, "nothing open to begin with");

            bool closed = false;
            Assert.That(TryOpen(_supplier, _main, () => closed = true), Is.True);
            Assert.That(IsOpen, Is.True);

            Open().Close();

            Assert.That(IsOpen, Is.False, "the slot is free again");
            Assert.That(closed, Is.True, "and the listing behind it was told to refresh");
        }

        [Test]
        public void ASecondDelivery_IsRefused_AndTheFirstOneIsLeftUntouched()
        {
            TryOpen(_supplier, _main);
            PurchaseInvoiceForm first = Open();
            Type(first, Line(3));

            Assert.That(TryOpen(_supplier, _main), Is.False,
                "two windows would file into the same draft, and the second save would erase the first");
            Assert.That(Open(), Is.SameAs(first), "the typing already done is still the open one");
        }

        [Test]
        public void ADeliveryPutAsideMinimized_ComesBackWhenANewOneIsAskedFor()
        {
            TryOpen(_supplier, _main);
            PurchaseInvoiceForm first = Open();
            Type(first, Line(3));
            first.WindowState = FormWindowState.Minimized;

            TryOpen(_supplier, _main);      // pharmacist forgot, and clicked "new invoice" again

            Assert.That(first.WindowState, Is.Not.EqualTo(FormWindowState.Minimized),
                "restoring it is the only way they will find the three boxes they already typed");
        }

        [Test]
        public void ADelivery_IsOwnedByTheMainWindow_NotByTheDialogThatOpenedIt()
        {
            // The listings that open a delivery are modal dialogs, and they close immediately after.
            // A window owned by one of those would be destroyed along with it.
            var listing = new Form { Owner = _main };
            listing.CreateControl();

            TryOpen(_supplier, listing);
            PurchaseInvoiceForm form = Open();

            Assert.That(form.Owner, Is.SameAs(_main));
            listing.Close();
            Assert.That(form.IsDisposed, Is.False, "the delivery outlives the listing that opened it");
            listing.Dispose();
        }

        // ---------------- logging out ----------------

        [Test]
        public void Logout_ShutsAnOpenDelivery_WithoutLeavingADraftForTheNextPerson()
        {
            TryOpen(_supplier, _main);
            Type(Open(), Line(3));

            CloseOpen();                    // what MainForm.Logout calls, before clearing drafts

            Assert.That(IsOpen, Is.False, "no window belonging to the previous user is left on screen");
            Assert.That(PurchaseDrafts.Count, Is.Zero,
                "and it files nothing on the way out — the drafts are wiped a line later anyway");
        }

        [Test]
        public void Logout_WithNothingOpen_DoesNothing()
        {
            Assert.That(IsOpen, Is.False);
            Assert.DoesNotThrow(CloseOpen);
        }
    }
}
