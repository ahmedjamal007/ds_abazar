using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Dawaii.App;
using Dawaii.Core;
using Dawaii.App.Modules;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// The price screen's own state (V2.3.2): which items are ticked, and which changes are pending.
    ///
    /// Both used to be read off the DataGridView, which is rebuilt on every search, filter and preview
    /// — so ticking ten drugs and pressing the calculate button cleared all ten ticks, and the grid was
    /// the only record of what the user had chosen. These tests drive the real module and assert on
    /// what survives a rebuild, because that is the part a service test cannot reach.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class PricingModuleSelectionTests
    {
        private string _path;
        private PricingModule _mod;
        private int _panadol, _brufen, _vitc, _handPriced;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_pricing_ui_" + Guid.NewGuid().ToString("N") + ".db");
            var services = new AppServices(new SqliteConnectionFactory(_path));
            services.Initializer.ApplySchemaAndSeed();
            services.Initializer.EnsureDefaultAdmin();
            Session.Services = services;
            Session.CurrentUser = services.Users.GetByUsername("admin");

            _panadol = Drug(services, "zz-panadol", 60m);
            _brufen = Drug(services, "zz-brufen", 80m);
            _vitc = Drug(services, "zz-vitc", 20m);
            _handPriced = Drug(services, "zz-hand", 50m);
            services.Items.ApplySellingPrice(_handPriced, 50m, manual: true);

            // Pending prices and ticks are session state, so they survive a module — including into the
            // next test. Every test starts from an empty screen the way a fresh login would.
            PricingModule.ResetPending();

            _mod = new PricingModule();
            _mod.CreateControl();
            Search("zz-");                      // only our four, never the demo catalogue
        }

        [TearDown]
        public void TearDown()
        {
            _mod?.Dispose();
            PricingModule.ResetPending();
            Session.CurrentUser = null;
            Session.Services = null;
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static int Drug(AppServices s, string name, decimal perUnit)
        {
            int id = s.Items.Add(new Item
            {
                NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10,
                PurchasePrice = perUnit / 2, SellingPrice = perUnit, IsActive = true
            });
            s.Stock.AddBatch(new StockBatch
            {
                ItemId = id, QuantityUnits = 1000, StripsPerBox = 10, UnitsPerStrip = 10,
                BoxSellingPrice = perUnit * 100, ExpiryDate = DateTime.Today.AddYears(1)
            });
            return id;
        }

        // ---------------- reaching into the module ----------------

        // Static too: the pending prices and the ticks outlive the screen now, so they are static fields.
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private T Field<T>(string name) => (T)typeof(PricingModule).GetField(name, Any).GetValue(_mod);
        private void Call(string method, params object[] args)
            => typeof(PricingModule).GetMethods(Any)
                .First(m => m.Name == method && m.GetParameters().Length == args.Length)
                .Invoke(_mod, args);

        private HashSet<int> Selected => Field<HashSet<int>>("_selected");
        private DataGridView Grid => Field<DataGridView>("_grid");
        private NumericUpDown Multiplier => Field<NumericUpDown>("_multiplier");
        private ComboBox Filter => Field<ComboBox>("_filter");
        private CheckBox IncludeManual => Field<CheckBox>("_includeManual");

        private IDictionary Pending => (IDictionary)typeof(PricingModule).GetField("_pending", Any).GetValue(_mod);

        /// <summary>Runs the calculation the way the button does, minus the message box it ends with.</summary>
        private string Plan(PriceOperation op)
        {
            MethodInfo build = typeof(PricingModule).GetMethod("BuildPlan", Any);
            MethodInfo selected = typeof(PricingModule).GetMethod("SelectedOrCurrentIds", Any);
            var ids = (List<int>)selected.Invoke(_mod, new object[0]);
            try { return (string)build.Invoke(_mod, new object[] { op, ids }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private void Search(string term)
        {
            Field<TextBox>("_search").Text = term;
            Call("Reload");
        }

        /// <summary>Ticks a row the way a mouse click does — through the grid cell, not the set.</summary>
        private void TickInGrid(int itemId, bool ticked)
        {
            foreach (DataGridViewRow row in Grid.Rows)
            {
                var bound = row.DataBoundItem;
                int id = (int)bound.GetType().GetProperty("Id").GetValue(bound);
                if (id != itemId) continue;
                row.Cells["Selected"].Value = ticked;
                // The grid raises CellValueChanged for a programmatic set, which is the same handler a
                // click goes through once CommitEdit has run.
                return;
            }
            Assert.Fail("item " + itemId + " is not on screen");
        }

        private List<int> VisibleIds() => Grid.Rows.Cast<DataGridViewRow>()
            .Select(r => (int)r.DataBoundItem.GetType().GetProperty("Id").GetValue(r.DataBoundItem))
            .ToList();

        private bool TickedInGrid(int itemId) => Grid.Rows.Cast<DataGridViewRow>()
            .Where(r => (int)r.DataBoundItem.GetType().GetProperty("Id").GetValue(r.DataBoundItem) == itemId)
            .Select(r => Convert.ToBoolean(r.Cells["Selected"].Value ?? false))
            .FirstOrDefault();

        // ---------------- selection survives ----------------

        [Test]
        public void TickingARow_RecordsTheItemId()
        {
            TickInGrid(_panadol, true);
            Assert.That(Selected, Is.EquivalentTo(new[] { _panadol }));

            TickInGrid(_panadol, false);
            Assert.That(Selected, Is.Empty);
        }

        [Test]
        public void SelectionSurvivesAReload_WhichIsWhatPreviewingDoes()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_brufen, true);

            Call("Reload");

            Assert.That(Selected, Is.EquivalentTo(new[] { _panadol, _brufen }), "the ids are the record");
            Assert.That(TickedInGrid(_panadol), Is.True, "and the rebuilt rows show them ticked again");
            Assert.That(TickedInGrid(_brufen), Is.True);
            Assert.That(TickedInGrid(_vitc), Is.False);
        }

        [Test]
        public void SelectionSurvivesCalculating_SoASecondOperationNeedsNoReTicking()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_brufen, true);
            Multiplier.Value = 1.30m;

            Plan(PriceOperation.Increase);

            Assert.That(Selected, Is.EquivalentTo(new[] { _panadol, _brufen }));
            Assert.That(Pending.Count, Is.EqualTo(2));
            // And the ticks are still ON SCREEN — the user's complaint was visual: the boxes cleared.
            Assert.That(TickedInGrid(_panadol), Is.True, "the tick survived the rebuild the preview causes");
            Assert.That(TickedInGrid(_brufen), Is.True);
        }

        [Test]
        public void AnItemFilteredOutOfSight_IsDroppedFromTheSelection()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_handPriced, true);

            Filter.SelectedIndex = 2;                 // أسعار يدوية
            Call("Reload");

            Assert.That(VisibleIds(), Is.EquivalentTo(new[] { _handPriced }));
            Assert.That(Selected, Is.EquivalentTo(new[] { _handPriced }),
                "acting on the selection must never reach a row the user cannot see");
            Assert.That(TickedInGrid(_handPriced), Is.True, "and the one still visible keeps its tick");
        }

        [Test]
        public void SelectAll_TakesTheVisibleRowsOnly()
        {
            Filter.SelectedIndex = 2;                 // أسعار يدوية — one row
            Call("Reload");

            Call("SetAllSelected", true);

            Assert.That(Selected, Is.EquivalentTo(new[] { _handPriced }),
                "select-all while filtered must not arm the whole pharmacy");

            // And it survives the next rebuild, like any other selection.
            Call("Reload");
            Assert.That(TickedInGrid(_handPriced), Is.True);
        }

        // ---------------- pending changes survive ----------------

        [Test]
        public void PendingChangesSurviveSearchingAndFiltering()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_brufen, true);
            Multiplier.Value = 1.30m;
            Plan(PriceOperation.Increase);
            Assert.That(Pending.Count, Is.EqualTo(2));

            Search("zz-panadol");                     // narrow right down
            Assert.That(Pending.Count, Is.EqualTo(2), "filtering changes what is shown, not what is pending");

            Filter.SelectedIndex = 3;                 // لديها تغيير معلّق
            Call("Reload");
            Assert.That(VisibleIds(), Is.EquivalentTo(new[] { _panadol }), "…and the pending filter still finds it");

            Search("zz-");
            Call("Reload");
            Assert.That(Pending.Count, Is.EqualTo(2), "both are still waiting when the search is cleared");
        }

        [Test]
        public void APendingManualPrice_IsNotOverwrittenByALaterMultiplier()
        {
            // Calculate for two, then hand-price one of them, then run the multiplier again.
            TickInGrid(_panadol, true);
            TickInGrid(_brufen, true);
            Multiplier.Value = 1.30m;
            Plan(PriceOperation.Increase);

            // Stand in for the edit dialog: mark panadol's pending change as hand-typed.
            object pendingPanadol = Pending[_panadol];
            FieldInfo op = pendingPanadol.GetType().GetField("Operation", Any);
            op.SetValue(pendingPanadol, PriceOperation.Manual);
            FieldInfo mult = pendingPanadol.GetType().GetField("Multiplier", Any);
            mult.SetValue(pendingPanadol, null);
            decimal typed = (decimal)pendingPanadol.GetType().GetField("Figures", Any)
                .GetValue(pendingPanadol).GetType().GetProperty("BoxPrice")
                .GetValue(pendingPanadol.GetType().GetField("Figures", Any).GetValue(pendingPanadol));

            IncludeManual.Checked = false;
            Multiplier.Value = 1.50m;
            Plan(PriceOperation.Increase);

            object after = Pending[_panadol];
            object figures = after.GetType().GetField("Figures", Any).GetValue(after);
            decimal stillThere = (decimal)figures.GetType().GetProperty("BoxPrice").GetValue(figures);

            Assert.That(stillThere, Is.EqualTo(typed), "the hand-typed pending price was left alone");
            Assert.That((PriceOperation)op.GetValue(after), Is.EqualTo(PriceOperation.Manual));
        }

        // ---------------- the operation reaches the screen ----------------

        [Test]
        public void ADecreaseShowsAsADecrease_InTheGrid()
        {
            TickInGrid(_panadol, true);
            Multiplier.Value = 0.90m;
            Plan(PriceOperation.Decrease);

            var row = Grid.Rows.Cast<DataGridViewRow>()
                .First(r => (int)r.DataBoundItem.GetType().GetProperty("Id").GetValue(r.DataBoundItem) == _panadol)
                .DataBoundItem;

            Assert.That(row.GetType().GetProperty("Operation").GetValue(row), Is.EqualTo("تخفيض"));
            Assert.That(row.GetType().GetProperty("Multiplier").GetValue(row), Is.EqualTo("0.90"));
            Assert.That(row.GetType().GetProperty("NewBox").GetValue(row), Does.Contain("5,400"),
                "6,000 × 0.90 — the price came down, it was not reduced by 0.90");
        }

        [Test]
        public void AWrongMultiplierForTheOperation_ChangesNothing()
        {
            TickInGrid(_panadol, true);
            Multiplier.Value = 0.90m;

            // 0.90 is not an increase — the service refuses it, and the screen turns that into a message.
            Assert.Throws<ValidationException>(() => Plan(PriceOperation.Increase));
            Assert.That(Pending, Is.Empty, "no pending row was created");
        }

        // ---------------- stepping away and coming back (V2.3.2) ----------------

        /// <summary>
        /// Leaves this screen and comes back to a fresh one, the way the sidebar does — MainForm
        /// disposes the module it is leaving and builds the next from scratch.
        /// </summary>
        private void NavigateAwayAndBack()
        {
            _mod.Dispose();
            _mod = new PricingModule();
            _mod.CreateControl();
            Search("zz-");
        }

        [Test]
        public void PricesCalculatedButNotApplied_SurviveATripToAnotherScreen()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_brufen, true);
            Multiplier.Value = 1.30m;
            Plan(PriceOperation.Increase);
            Assert.That(Pending.Count, Is.EqualTo(2), "two prices worked out and waiting");

            NavigateAwayAndBack();      // a customer walks in; the pharmacist serves them

            Assert.That(Pending.Count, Is.EqualTo(2),
                "coming back to an empty screen would mean doing the whole calculation again");
            Assert.That(Pending.Contains(_panadol), Is.True);
            Assert.That(Pending.Contains(_brufen), Is.True);
        }

        [Test]
        public void TheItemsTicked_SurviveATripToAnotherScreen()
        {
            TickInGrid(_panadol, true);
            TickInGrid(_vitc, true);

            NavigateAwayAndBack();

            Assert.That(Selected, Is.EquivalentTo(new[] { _panadol, _vitc }),
                "picking forty drugs out of a catalogue is the slow part — it must not be thrown away");
        }

        [Test]
        public void APendingPrice_IsStillShownOnTheRowAfterComingBack()
        {
            TickInGrid(_panadol, true);
            Multiplier.Value = 1.30m;
            Plan(PriceOperation.Increase);

            NavigateAwayAndBack();

            object row = Grid.Rows.Cast<DataGridViewRow>()
                .First(r => (int)r.DataBoundItem.GetType().GetProperty("Id").GetValue(r.DataBoundItem) == _panadol)
                .DataBoundItem;

            Assert.That(row.GetType().GetProperty("HasPending").GetValue(row), Is.True,
                "kept in memory but not drawn would be worse than losing it — the user could not see it");
            Assert.That(row.GetType().GetProperty("Operation").GetValue(row), Is.EqualTo("زيادة"));
        }

        [Test]
        public void LoggingOut_ForgetsPricesTheNextUserNeverCalculated()
        {
            TickInGrid(_panadol, true);
            Multiplier.Value = 1.30m;
            Plan(PriceOperation.Increase);

            PricingModule.ResetPending();       // what MainForm.Logout calls

            Assert.That(Pending, Is.Empty);
            Assert.That(Selected, Is.Empty,
                "an unapplied price increase is the last thing the next user should inherit unseen");
        }
    }
}
