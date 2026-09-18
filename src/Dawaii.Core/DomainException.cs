using System;

namespace Dawaii.Core
{
    /// <summary>Base class for expected business-rule failures (shown to the user, not bugs).</summary>
    public class DomainException : Exception
    {
        public DomainException(string message) : base(message) { }
    }

    /// <summary>Raised when a user lacks the role required for an action (SRS 2.1, NFR-04).</summary>
    public class PermissionDeniedException : DomainException
    {
        public PermissionDeniedException(string message) : base(message) { }
        public PermissionDeniedException() : base("هذا الإجراء يتطلب صلاحية المدير.") { }
    }

    /// <summary>Raised when input fails a business validation rule.</summary>
    public class ValidationException : DomainException
    {
        public ValidationException(string message) : base(message) { }
    }

    /// <summary>Raised when there is not enough sellable stock to satisfy a request (FR-POS/FEFO).</summary>
    public class InsufficientStockException : DomainException
    {
        public int ItemId { get; }
        public int RequestedUnits { get; }
        public int AvailableUnits { get; }

        public InsufficientStockException(int itemId, int requestedUnits, int availableUnits)
            : this(itemId, requestedUnits, availableUnits,
                   $"الكمية غير كافية في المخزون (المطلوب {requestedUnits}، المتوفر {availableUnits}).")
        {
        }

        /// <summary>Same failure with a caller-supplied message, for screens that can name the item
        /// (e.g. "بنادول غير متوفر في المخزون") instead of the generic quantity wording.</summary>
        public InsufficientStockException(int itemId, int requestedUnits, int availableUnits, string message)
            : base(message)
        {
            ItemId = itemId;
            RequestedUnits = requestedUnits;
            AvailableUnits = availableUnits;
        }
    }

    /// <summary>Raised when a code is already assigned to a different item (FR-QRC-06).</summary>
    public class DuplicateCodeException : DomainException
    {
        public string Code { get; }
        public int ExistingItemId { get; }

        public DuplicateCodeException(string code, int existingItemId)
            : base($"الرمز \"{code}\" مستخدم بالفعل لصنف آخر.")
        {
            Code = code;
            ExistingItemId = existingItemId;
        }
    }
}
