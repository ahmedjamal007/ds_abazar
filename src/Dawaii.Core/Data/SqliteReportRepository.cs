using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.Core.Data
{
    public class SqliteReportRepository : IReportRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqliteReportRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public IReadOnlyList<BestSellerRow> BestSellers(DateTime fromInclusive, DateTime toExclusive, int limit)
            => _db.Query(
                "SELECT i.id, i.name_en, i.generic_name, SUM(sl.quantity * sl.units_each) AS units, SUM(sl.line_total) AS rev " +
                "FROM sale_lines sl " +
                "JOIN sales s ON sl.sale_id = s.id " +
                "JOIN items i ON sl.item_id = i.id " +
                "WHERE s.status='Completed' AND s.created_at >= @f AND s.created_at < @t " +
                "GROUP BY i.id, i.name_en, i.generic_name ORDER BY units DESC LIMIT @n",
                r => new BestSellerRow
                {
                    ItemId = Convert.ToInt32(r.GetValue(0)),
                    Name = Item.Display(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
                    UnitsSold = Convert.ToInt32(r.GetValue(3)),
                    Revenue = decimal.Round(Convert.ToDecimal(r.GetValue(4)), 2)
                },
                ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)), ("@n", limit));

        public IReadOnlyList<DeadStockRow> DeadStock(int days)
            => _db.Query(
                // Every column the item mapper reads has to be listed here. A name it cannot find does
                // not fail loudly — SQLite's reader answers -1 for an unknown column and the mapper takes
                // the default — so an omission shows up as a plausible-looking 0 on the report rather
                // than as an error. max_quantity and substitute_of were missing exactly that way.
                "SELECT i.id, i.name_en, i.generic_name, i.units_per_strip, " +
                "       i.strips_per_box, i.purchase_price, i.selling_price, i.manual_price, i.min_quantity, " +
                "       i.max_quantity, i.expiry_warn_days, i.substitute_of, " +
                "       i.is_active, i.created_at, i.updated_at, st.units AS avail " +
                "FROM items i " +
                "JOIN (SELECT item_id, SUM(quantity_units) AS units FROM stock_batches " +
                "      WHERE is_disposed=0 GROUP BY item_id HAVING SUM(quantity_units) > 0) st ON st.item_id = i.id " +
                "WHERE i.is_active=1 AND i.id NOT IN (" +
                "   SELECT DISTINCT sl.item_id FROM sale_lines sl JOIN sales s ON sl.sale_id=s.id " +
                "   WHERE s.status='Completed' AND s.created_at >= @since) " +
                "ORDER BY avail DESC",
                r => new DeadStockRow
                {
                    Item = SqliteItemRepository.Map(r),
                    AvailableUnits = Convert.ToInt32(r.GetValue(r.GetOrdinal("avail")))
                },
                ("@since", Db.Time(DateTime.Now.AddDays(-days))));
    }
}
