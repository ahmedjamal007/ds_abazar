using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class CodeServiceTests
    {
        private FakeItemCodeRepository _codes;
        private FakeItemRepository _items;
        private FakeAuditRepository _audit;
        private CodeService _svc;
        private User _admin, _cashier, _fullEmployee;
        private int _panadol, _amoxil;

        [SetUp]
        public void SetUp()
        {
            _codes = new FakeItemCodeRepository();
            _items = new FakeItemRepository();
            _audit = new FakeAuditRepository();
            _svc = new CodeService(_codes, _items, _audit);
            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };
            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };
            _fullEmployee = new User { Id = 3, Role = Role.FullEmployee, IsActive = true };
            _panadol = _items.Add(new Item { NameEn = "بنادول", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true });
            _amoxil = _items.Add(new Item { NameEn = "أموكسيل", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true });
        }

        [Test]
        public void AssignThenResolve_FindsItem()
        {
            _svc.SetCode(_admin, _panadol, "6291000123456");
            Assert.That(_svc.ResolveItemId("6291000123456"), Is.EqualTo(_panadol));
        }

        [Test]
        public void Resolve_UnknownCode_ReturnsNull()
        {
            Assert.That(_svc.ResolveItemId("does-not-exist"), Is.Null);
        }

        [Test]
        public void AssignSameCodeToDifferentItem_Throws_FR_QRC_06()
        {
            _svc.SetCode(_admin, _panadol, "CODE-1");
            var ex = Assert.Throws<DuplicateCodeException>(() => _svc.SetCode(_admin, _amoxil, "CODE-1"));
            Assert.That(ex.ExistingItemId, Is.EqualTo(_panadol));
        }

        [Test]
        public void AssignSameCodeToSameItem_IsNoOp()
        {
            _svc.SetCode(_admin, _panadol, "CODE-1");
            Assert.DoesNotThrow(() => _svc.SetCode(_admin, _panadol, "CODE-1"));
            Assert.That(_codes.Codes.Count(c => c.ItemId == _panadol), Is.EqualTo(1));
        }

        [Test]
        public void SetCode_ReplacesThePrevious_AndFreesIt_V19()
        {
            // One barcode per item: re-labelling a drug releases the code it used to carry, so that
            // code can be given to another item instead of staying owned forever.
            _svc.SetCode(_admin, _panadol, "OLD");
            _svc.SetCode(_admin, _panadol, "NEW");

            Assert.That(_codes.Codes.Count(c => c.ItemId == _panadol), Is.EqualTo(1), "one code per item");
            Assert.That(_svc.GetCode(_panadol), Is.EqualTo("NEW"));
            Assert.That(_svc.ResolveItemId("OLD"), Is.Null, "the replaced code no longer resolves");
            Assert.DoesNotThrow(() => _svc.SetCode(_admin, _amoxil, "OLD"), "and is free for another item");
        }

        [Test]
        public void RemoveCode_ClearsIt_AndFreesItForAnotherItem_V19()
        {
            _svc.SetCode(_admin, _panadol, "SHARED");
            _svc.RemoveCode(_admin, _panadol);

            Assert.That(_svc.GetCode(_panadol), Is.Null);
            Assert.That(_svc.ResolveItemId("SHARED"), Is.Null);
            Assert.DoesNotThrow(() => _svc.SetCode(_admin, _amoxil, "SHARED"));
        }

        [Test]
        public void Resolve_IgnoresCaseAndScannerWhitespace_V19()
        {
            // A QR reader may deliver the content padded or with a trailing newline, and reads it back
            // in whatever case it was encoded — none of that should stop the item being found.
            _svc.SetCode(_admin, _panadol, "DW-7");

            Assert.That(_svc.ResolveItemId("dw-7"), Is.EqualTo(_panadol), "case");
            Assert.That(_svc.ResolveItemId("  DW-7  "), Is.EqualTo(_panadol), "padding");
            Assert.That(_svc.ResolveItemId("DW-7\r\n"), Is.EqualTo(_panadol), "scanner newline");
        }

        [Test]
        public void SetCode_StripsControlCharactersBeforeStoring_V19()
        {
            _svc.SetCode(_admin, _panadol, "\t6291000123456\r\n");
            Assert.That(_svc.GetCode(_panadol), Is.EqualTo("6291000123456"));
            Assert.That(_svc.ResolveItemId("6291000123456"), Is.EqualTo(_panadol));
        }

        [Test]
        public void AssignCode_ByFullEmployee_Allowed()
        {
            // Codes follow the catalog: whoever adds an item attaches its barcode in the same step.
            _svc.SetCode(_fullEmployee, _panadol, "X");
            Assert.That(_svc.ResolveItemId("X"), Is.EqualTo(_panadol));
        }

        [Test]
        public void AssignCode_ByPlainCashierOrSignedOut_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.SetCode(_cashier, _panadol, "X"));
            Assert.Throws<PermissionDeniedException>(() => _svc.SetCode(null, _panadol, "X"));
        }

        [Test]
        public void IsCodeAvailable_TrueForFreeOrOwnCode_FalseForOthers()
        {
            _svc.SetCode(_admin, _panadol, "OWN");
            Assert.That(_svc.IsCodeAvailable("FREE", _amoxil), Is.True);
            Assert.That(_svc.IsCodeAvailable("OWN", _panadol), Is.True);   // same item
            Assert.That(_svc.IsCodeAvailable("OWN", _amoxil), Is.False);   // other item
        }

        [Test]
        public void EnsureInternalCode_GeneratesWhenMissing_ReusesWhenPresent()
        {
            string code1 = _svc.EnsureInternalCode(_admin, _panadol);
            Assert.That(code1, Is.EqualTo("DW-" + _panadol));
            string code2 = _svc.EnsureInternalCode(_admin, _panadol);
            Assert.That(code2, Is.EqualTo(code1));
            Assert.That(_codes.Codes.Count(c => c.ItemId == _panadol), Is.EqualTo(1));
        }

        [Test]
        public void ScanAtPos_AddsQtyOne_RescanIncrements_FR_QRC_07()
        {
            // Simulate POS scan behaviour: resolve the code, then add/increment in the cart.
            _svc.SetCode(_admin, _panadol, "SCAN1");
            var cart = new List<CartLine>();

            for (int scan = 0; scan < 3; scan++)
            {
                int itemId = _svc.ResolveItemId("SCAN1").Value;
                CartLine line = cart.FirstOrDefault(c => c.ItemId == itemId);
                if (line == null) cart.Add(new CartLine { ItemId = itemId, UnitType = UnitType.Unit, Quantity = 1 });
                else line.Quantity += 1;
            }

            Assert.That(cart.Count, Is.EqualTo(1));
            Assert.That(cart[0].Quantity, Is.EqualTo(3));
        }

        [Test]
        public void QrCodeGenerator_ProducesPngBytes()
        {
            byte[] png = QrCodeGenerator.PngBytes("DW-1", 5);
            Assert.That(png.Length, Is.GreaterThan(0));
            // PNG signature 0x89 'P' 'N' 'G'
            Assert.That(png[0], Is.EqualTo(0x89));
            Assert.That(png[1], Is.EqualTo((byte)'P'));
            Assert.That(png[2], Is.EqualTo((byte)'N'));
            Assert.That(png[3], Is.EqualTo((byte)'G'));
        }
    }
}
