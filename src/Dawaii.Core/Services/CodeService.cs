using System.Text;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// QR/barcode identification (SRS 3.6). Codes are plain text and the relationship is one-to-one
    /// (V1.9): an item has a single code and a code belongs to a single item (FR-QRC-06). Setting a
    /// code replaces the item's previous one — there is no list to curate, because a drug has one
    /// barcode on its box. A drug that has none can be given an internal QR code to print and stick on
    /// (FR-QRC-05); scanning either kind goes through the same lookup, since a QR reader and a barcode
    /// reader both just type their content.
    /// Setting codes follows the catalog (V1.8): whoever may manage inventory may do it. Resolving
    /// codes is open to everyone.
    /// </summary>
    public class CodeService
    {
        private readonly IItemCodeRepository _codes;
        private readonly IItemRepository _items;
        private readonly IAuditRepository _audit;

        public CodeService(IItemCodeRepository codes, IItemRepository items, IAuditRepository audit)
        {
            _codes = codes;
            _items = items;
            _audit = audit;
        }

        /// <summary>Returns the item id for a scanned/typed code, or null if unknown (FR-QRC-03/04).</summary>
        public int? ResolveItemId(string code)
        {
            string scanned = Normalize(code);
            if (scanned.Length == 0) return null;
            ItemCode found = _codes.FindByCode(scanned);
            return found?.ItemId;
        }

        /// <summary>Returns the item for a scanned code, or null if unknown.</summary>
        public Item ResolveItem(string code)
        {
            int? id = ResolveItemId(code);
            return id.HasValue ? _items.GetById(id.Value) : null;
        }

        /// <summary>The item's barcode/QR code, or null when it has none.</summary>
        public string GetCode(int itemId) => _codes.GetByItem(itemId)?.Code;

        /// <summary>
        /// Sets the item's code, replacing any code it already had. Rejects a code owned by a different
        /// item (FR-QRC-06); re-setting the same code on the same item is a no-op.
        /// </summary>
        public void SetCode(User user, int itemId, string code)
        {
            RequireStaff(user);
            code = Normalize(code);
            if (code.Length == 0) throw new ValidationException("الرمز فارغ.");
            if (_items.GetById(itemId) == null) throw new ValidationException("الصنف غير موجود.");

            ItemCode existing = _codes.FindByCode(code);
            if (existing != null)
            {
                if (existing.ItemId == itemId) return;                 // already this item's code
                throw new DuplicateCodeException(code, existing.ItemId); // owned by another item
            }

            _codes.SetForItem(itemId, code);
            _audit.Log(user, "SetCode", "item_codes", itemId, code);
        }

        /// <summary>Clears the item's code, freeing that barcode for another item.</summary>
        public void RemoveCode(User user, int itemId)
        {
            RequireStaff(user);
            _codes.RemoveForItem(itemId);
            _audit.Log(user, "RemoveCode", "item_codes", itemId, null);
        }

        /// <summary>
        /// Returns the item's code, generating and assigning an internal one ("DW-{id}") if it has none —
        /// used to print a QR label for items without a manufacturer barcode (FR-QRC-05).
        /// </summary>
        public string EnsureInternalCode(User user, int itemId)
        {
            RequireStaff(user);
            string existing = GetCode(itemId);
            if (!string.IsNullOrEmpty(existing)) return existing;
            string code = Printing.QrCodeGenerator.InternalCodeFor(itemId);
            SetCode(user, itemId, code);
            return code;
        }

        /// <summary>True if the code is free to give to <paramref name="itemId"/> (not owned by another).</summary>
        public bool IsCodeAvailable(string code, int itemId)
        {
            string wanted = Normalize(code);
            if (wanted.Length == 0) return false;
            ItemCode existing = _codes.FindByCode(wanted);
            return existing == null || existing.ItemId == itemId;
        }

        /// <summary>
        /// What the reader actually delivered, cleaned up: surrounding whitespace and any control
        /// characters removed. Barcode wedges send a clean line, but QR readers often append a CR/LF or
        /// a tab to the content, and that would otherwise be stored as part of the code — making it
        /// impossible to ever scan back.
        /// </summary>
        private static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return string.Empty;
            var clean = new StringBuilder(code.Length);
            foreach (char c in code)
                if (!char.IsControl(c)) clean.Append(c);
            return clean.ToString().Trim();
        }

        private static void RequireStaff(User user)
            => Guard.RequireInventoryAccess(user,
                "إدارة الرموز متاحة للمدير أو الموظف ذي الامتيازات فقط.");
    }
}
