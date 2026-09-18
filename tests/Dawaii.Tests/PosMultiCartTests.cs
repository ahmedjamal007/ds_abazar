using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Dawaii.App;
using Dawaii.App.Modules;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Several customers at the till at once (V2.3): the POS keeps one cart per pending invoice and
    /// shows one of them. The guarantee that matters is that nothing leaks between them — a line,
    /// a discount, a credit customer, a payment method — however the cashier moves around. So these
    /// tests drive the real module through its private entry points and check every cart after every
    /// move, rather than testing a model of the carts that the screen might not actually use.
    ///
    /// Built against a real SQLite database because adding a line asks the stock for an expiry warning.
    /// Completing a sale is not driven here — it shows a message box — and is unchanged anyway: it
    /// calls the same service the single-cart POS did.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class PosMultiCartTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private PosModule _pos;
        private Item _panadol, _brufen, _vitc;
        private Customer _ali;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_carts_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var services = new AppServices(_db);
            services.Initializer.ApplySchemaAndSeed();
            services.Initializer.EnsureDefaultAdmin();
            Session.Services = services;
            Session.CurrentUser = services.Users.GetByUsername("admin");

            _panadol = Drug(services, "panadol", 60m);
            _brufen = Drug(services, "brufen", 80m);
            _vitc = Drug(services, "vitamin c", 20m);
            int aliId = services.Customers.Add(new Customer { Name = "علي" });
            _ali = services.Customers.GetById(aliId);

            PosModule.ResetCarts();
            _pos = new PosModule();
            _pos.CreateControl();
        }

        [TearDown]
        public void TearDown()
        {
            _pos?.Dispose();
            PosModule.ResetCarts();
            Session.CurrentUser = null;
            Session.Services = null;
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static Item Drug(AppServices s, string name, decimal perUnit)
        {
            int id = s.Items.Add(new Item { NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10, PurchasePrice = perUnit / 2, SellingPrice = perUnit, IsActive = true });
            s.Stock.AddBatch(new StockBatch { ItemId = id, QuantityUnits = 1000, StripsPerBox = 10, UnitsPerStrip = 10, BoxSellingPrice = perUnit * 100, ExpiryDate = DateTime.Today.AddYears(1) });
            return s.Items.GetById(id);
        }

        // ---------------- reaching into the module ----------------

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private void Call(string method, params object[] args)
        {
            MethodInfo m = typeof(PosModule).GetMethods(Any).First(x => x.Name == method && x.GetParameters().Length == args.Length);
            m.Invoke(_pos, args);
        }

        private T Field<T>(string name) => (T)typeof(PosModule).GetField(name, Any).GetValue(_pos);
        private static object StaticField(string name) => typeof(PosModule).GetField(name, Any).GetValue(null);

        /// <summary>The active cart's lines, as (item id, qty), through the same property the POS reads.</summary>
        private List<(int itemId, int qty)> ActiveLines()
        {
            var cart = (IEnumerable)typeof(PosModule).GetProperty("_cart", Any).GetValue(_pos);
            var lines = new List<(int, int)>();
            foreach (object row in cart)
            {
                Type rt = row.GetType();
                var item = (Item)rt.GetField("Item").GetValue(row);
                int qty = (int)rt.GetField("Qty").GetValue(row);
                lines.Add((item.Id, qty));
            }
            return lines;
        }

        private Customer ActiveCustomer() => (Customer)typeof(PosModule).GetProperty("_creditCustomer", Any).GetValue(_pos);
        private void SetActiveCustomer(Customer c) => typeof(PosModule).GetProperty("_creditCustomer", Any).SetValue(_pos, c);

        private int ActiveNumber()
        {
            object active = StaticField("_active");
            return (int)active.GetType().GetField("Number").GetValue(active);
        }

        private List<int> CartNumbers()
        {
            var carts = (IEnumerable)StaticField("Carts");
            var numbers = new List<int>();
            foreach (object c in carts) numbers.Add((int)c.GetType().GetField("Number").GetValue(c));
            numbers.Sort();
            return numbers;
        }

        private NumericUpDown Discount => Field<NumericUpDown>("_discount");
        private ComboBox Payment => Field<ComboBox>("_payment");
        private Label Warn => Field<Label>("_warn");

        // ---------------- independence ----------------

        [Test]
        public void ANewCart_StartsEmpty_AndTheFirstKeepsEveryLine()
        {
            Call("AddItem", _panadol);
            Call("AddItem", _panadol);      // same drug again → qty 2
            Call("AddItem", _brufen);
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 2), (_brufen.Id, 1) }));

            Call("NewCart");
            Assert.That(ActiveNumber(), Is.EqualTo(2));
            Assert.That(ActiveLines(), Is.Empty, "customer 2 starts with nothing in the basket");

            Call("AddItem", _vitc);
            Call("SwitchToNumber", 1);
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 2), (_brufen.Id, 1) }),
                "customer 1's five minutes of deciding cost them nothing");

            Call("SwitchToNumber", 2);
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_vitc.Id, 1) }));
        }

        [Test]
        public void DiscountCustomerAndPaymentMethod_BelongToTheirOwnCart()
        {
            Call("AddItem", _panadol);
            Discount.Value = 500m;
            Payment.SelectedIndex = 2;          // فوري
            SetActiveCustomer(_ali);

            Call("NewCart");
            Assert.That(Discount.Value, Is.Zero, "a new customer has no discount");
            Assert.That(Payment.SelectedIndex, Is.Zero, "and pays cash unless told otherwise");
            Assert.That(ActiveCustomer(), Is.Null);

            Discount.Value = 75m;
            Payment.SelectedIndex = 1;          // بنكك

            Call("SwitchToNumber", 1);
            Assert.That(Discount.Value, Is.EqualTo(500m));
            Assert.That(Payment.SelectedIndex, Is.EqualTo(2));
            Assert.That(ActiveCustomer()?.Id, Is.EqualTo(_ali.Id), "the credit customer stayed on their own invoice");

            Call("SwitchToNumber", 2);
            Assert.That(Discount.Value, Is.EqualTo(75m));
            Assert.That(Payment.SelectedIndex, Is.EqualTo(1));
            Assert.That(ActiveCustomer(), Is.Null);
        }

        [Test]
        public void TheExpiryWarning_StaysWithTheCartItWasRaisedOn()
        {
            // A batch expiring next week raises a warning on the cart that took it.
            int soon = Session.Services.Items.Add(new Item { NameEn = "soon", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 5m, IsActive = true });
            Session.Services.Stock.AddBatch(new StockBatch { ItemId = soon, QuantityUnits = 10, StripsPerBox = 1, UnitsPerStrip = 1, BoxSellingPrice = 5m, ExpiryDate = DateTime.Today.AddDays(7) });

            Call("AddItem", Session.Services.Items.GetById(soon));
            Assert.That(Warn.Text, Does.Contain("تنبيه"));

            Call("NewCart");
            Assert.That(Warn.Text, Is.Empty, "the warning is not about this customer's basket");

            Call("SwitchToNumber", 1);
            Assert.That(Warn.Text, Does.Contain("تنبيه"));
        }

        [Test]
        public void RemovingALine_InOneCart_LeavesTheOthersAlone()
        {
            Call("AddItem", _panadol);
            Call("NewCart");
            Call("AddItem", _panadol);
            Call("AddItem", _brufen);

            Call("RemoveCartLine", 0);          // drop panadol from cart 2
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_brufen.Id, 1) }));

            Call("SwitchToNumber", 1);
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 1) }), "cart 1 still has its panadol");
        }

        // ---------------- numbering and closing ----------------

        [Test]
        public void Numbers_ReuseTheLowestFreeSlot()
        {
            Call("NewCart"); Call("NewCart");           // [1][2][3], on 3
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1, 2, 3 }));

            Call("SwitchToNumber", 2);
            Call("CloseActiveCart");                    // [1][3]
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1, 3 }));

            Call("NewCart");
            Assert.That(ActiveNumber(), Is.EqualTo(2), "the next customer takes the free slot, not [4]");
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void ClosingACart_MovesToTheNeighbourOnTheRight_ThenLeft_ThenAFreshOne()
        {
            Call("AddItem", _panadol);
            Call("NewCart"); Call("AddItem", _brufen);
            Call("NewCart"); Call("AddItem", _vitc);   // [1][2][3], on 3

            Call("SwitchToNumber", 2);
            Call("CloseActiveCart");                    // 2 goes → the one that was to its right
            Assert.That(ActiveNumber(), Is.EqualTo(3));
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_vitc.Id, 1) }), "and the screen shows THAT customer's basket");

            Call("CloseActiveCart");                    // 3 goes → nothing to the right, so the left
            Assert.That(ActiveNumber(), Is.EqualTo(1));
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 1) }));

            Call("CloseActiveCart");                    // the last one → a clean new invoice
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1 }));
            Assert.That(ActiveLines(), Is.Empty, "the POS is back to its normal new-sale state");
            Assert.That(Discount.Value, Is.Zero);
        }

        [Test]
        public void ClearingTheOnlyCart_EmptiesItInPlace_AsItAlwaysDid()
        {
            Call("AddItem", _panadol);
            // "مسح" asks first when there are lines; the confirmation cannot be driven here, so the
            // in-place branch is exercised through CloseActiveCart on a single cart instead.
            Call("CloseActiveCart");
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1 }));
            Assert.That(ActiveLines(), Is.Empty);
        }

        // ---------------- survival ----------------

        [Test]
        public void PendingInvoices_SurviveLeavingThePosScreen()
        {
            Call("AddItem", _panadol);
            Call("NewCart"); Call("AddItem", _brufen); Discount.Value = 40m;

            // Navigating to another screen disposes the module and builds a fresh one on return.
            _pos.Dispose();
            _pos = new PosModule();
            _pos.CreateControl();

            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1, 2 }), "both customers are still waiting");
            Assert.That(ActiveNumber(), Is.EqualTo(2), "on the invoice the cashier was working on");
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_brufen.Id, 1) }));
            Assert.That(Discount.Value, Is.EqualTo(40m));

            Call("SwitchToNumber", 1);
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 1) }));
        }

        [Test]
        public void Logout_ForgetsEveryPendingInvoice()
        {
            Call("AddItem", _panadol);
            Call("NewCart"); Call("AddItem", _brufen);

            PosModule.ResetCarts();               // what MainForm.Logout calls
            _pos.Dispose();
            _pos = new PosModule();
            _pos.CreateControl();

            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1 }));
            Assert.That(ActiveLines(), Is.Empty, "the next cashier does not inherit this one's customers");
        }

        // ---------------- the screen's own entry points ----------------
        //
        // The first cut of the multi-cart patch swallowed OnActivated and ProcessCmdKey while
        // rewriting the tab strip, and nothing here noticed: every test above drives AddItem directly.
        // The POS opened to an empty results grid and no shortcut worked. These pin both.

        [Test]
        public void OnActivated_LoadsTheSellableItems_IntoTheResultsGrid()
        {
            _pos.OnActivated();
            var results = Field<DataGridView>("_results");
            Assert.That(results.RowCount, Is.GreaterThan(0), "the grid must not open empty");

            // Our three plus the seeded demo catalogue; the exact number is not the point.
            var names = results.Rows.Cast<DataGridViewRow>()
                .Select(r => Convert.ToString(r.Cells[0].Value)).ToList();
            Assert.That(names, Does.Contain("panadol").And.Contain("brufen").And.Contain("vitamin c"));
        }

        private bool Key(Keys keys)
        {
            MethodInfo m = typeof(PosModule).GetMethod("ProcessCmdKey", Any);
            var msg = new Message();
            object[] args = { msg, keys };
            return (bool)m.Invoke(_pos, args);
        }

        [Test]
        public void CtrlN_OpensANewInvoice_AndCtrlDigit_SwitchesToIt()
        {
            Call("AddItem", _panadol);
            Assert.That(Key(Keys.Control | Keys.N), Is.True, "the shortcut is handled");
            Assert.That(CartNumbers(), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(ActiveNumber(), Is.EqualTo(2));

            Assert.That(Key(Keys.Control | Keys.D1), Is.True);
            Assert.That(ActiveNumber(), Is.EqualTo(1));
            Assert.That(ActiveLines(), Is.EqualTo(new[] { (_panadol.Id, 1) }));

            Assert.That(Key(Keys.Control | Keys.Tab), Is.True);
            Assert.That(ActiveNumber(), Is.EqualTo(2), "Ctrl+Tab cycles forward");
        }

        [Test]
        public void TheOriginalShortcuts_AreStillWired()
        {
            // F2 focuses the search box; Del with nothing highlighted falls through (returns false).
            Assert.That(Key(Keys.F2), Is.True);
            Assert.That(Key(Keys.Delete), Is.False, "nothing to remove, so the key is left to the control");
        }

        // ---------------- the strip ----------------

        [Test]
        public void TheStrip_ShowsOneButtonPerCart_PlusAdd_AndMarksTheActiveOne()
        {
            Call("AddItem", _panadol);
            Call("NewCart");
            Application.DoEvents();               // the rebuild is deferred past the click that caused it

            var tabs = Field<FlowLayoutPanel>("_tabs");
            var pills = tabs.Controls.OfType<Dawaii.App.Ui.PillButton>().ToList();

            Assert.That(pills.Count, Is.EqualTo(3), "[1] [2] and [+]");
            Assert.That(pills.Last().Text, Is.EqualTo("+"));
            Assert.That(pills[0].Text, Does.StartWith("1").And.Contain("(1)"), "cart 1 shows how many lines it holds");
            Assert.That(pills[0].Outline, Is.True, "not the active one");
            Assert.That(pills[1].Text, Is.EqualTo("2"));
            Assert.That(pills[1].Outline, Is.False, "the active invoice is the filled pill");
        }
    }
}
