using System;
using System.Collections.Generic;
using System.Linq;

namespace Dawaii.Core.Services
{
    /// <summary>Pure backup housekeeping rules (FR-BAK-01/03) — testable without touching disk/DB.</summary>
    public static class BackupPolicy
    {
        public const int DefaultWarnDays = 3;

        /// <summary>True if the last successful backup is older than <paramref name="warnDays"/> or there is none.</summary>
        public static bool NeedsWarning(DateTime? lastSuccessfulBackup, DateTime now, int warnDays = DefaultWarnDays)
        {
            if (lastSuccessfulBackup == null) return true;
            return (now.Date - lastSuccessfulBackup.Value.Date).Days >= warnDays;
        }

        /// <summary>
        /// Given backup file paths (any order) with their timestamps, returns the ones to delete so
        /// that only the newest <paramref name="keepLast"/> remain (FR-BAK-01 "keep last N").
        /// </summary>
        public static IReadOnlyList<T> ToPrune<T>(IEnumerable<T> items, Func<T, DateTime> timestamp, int keepLast)
        {
            if (keepLast < 0) keepLast = 0;
            return items.OrderByDescending(timestamp).Skip(keepLast).ToList();
        }
    }
}
