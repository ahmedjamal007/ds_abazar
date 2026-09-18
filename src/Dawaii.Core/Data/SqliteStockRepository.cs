using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteStockRepository : IStockRepository
    {
        private const string Cols =
            "id, item_id, quantity_units, expiry_date, batch_number, strips_per_box, units_per_strip, " +
            "box_purchase_price, box_selling_price, received_at, is_disposed";

        private readonly IDbConnectionFactory _db;

        public SqliteStockRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public IReadOnlyList<StockBatch> GetBatches(int itemId, bool includeDisposed = false)
            => _db.Query(
                $"SELECT {Cols} FROM stock_batches WHERE item_id=@i {(includeDisposed ? "" : "AND is_disposed=0")} " +
                "ORDER BY (expiry_date IS NULL), expiry_date, id",
                Map, ("@i", itemId));

        public IReadOnlyList<StockBatch> GetSellableBatches(int itemId)
            => _db.Query(
                $"SELECT {Cols} FROM stock_batches WHERE item_id=@i AND is_disposed=0 AND quantity_units>0 " +
                "ORDER BY (expiry_date IS NULL), expiry_date, id",
                Map, ("@i", itemId));

        public StockBatch GetBatch(int batchId)
            => _db.QueryOne($"SELECT {Cols} FROM stock_batches WHERE id=@id", Map, ("@id", batchId));

        public IReadOnlyList<StockBatch> GetActiveBatches()
            => _db.Query(
                $"SELECT {Cols} FROM stock_batches WHERE is_disposed=0 AND quantity_units>0 " +
                "ORDER BY (expiry_date IS NULL), expiry_date, id", Map);

        public IReadOnlyList<StockBatch> GetExpiringBefore(DateTime cutoff)
            => _db.Query(
                $"SELECT {Cols} FROM stock_batches " +
                "WHERE is_disposed=0 AND quantity_units>0 AND expiry_date IS NOT NULL AND expiry_date <= @c " +
                "ORDER BY expiry_date, id",
                Map, ("@c", cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        public void ApplySellingPriceToAllBatches(int itemId, decimal perUnitSellingPrice)
            => _db.Execute(
                "UPDATE stock_batches SET box_selling_price = @p * strips_per_box * units_per_strip " +
                "WHERE item_id=@i AND is_disposed=0",
                ("@p", Db.Money(perUnitSellingPrice)), ("@i", itemId));

        public int AddBatch(StockBatch b)
            => _db.InsertId(
                "INSERT INTO stock_batches (item_id, quantity_units, expiry_date, batch_number, " +
                "  strips_per_box, units_per_strip, box_purchase_price, box_selling_price, is_disposed) " +
                "VALUES (@i, @q, @e, @bn, @spb, @ups, @bp, @bs, 0)",
                ("@i", b.ItemId), ("@q", b.QuantityUnits), ("@e", ExpiryParam(b.ExpiryDate)),
                ("@bn", Db.Text(b.BatchNumber)), ("@spb", b.StripsPerBox), ("@ups", b.UnitsPerStrip),
                ("@bp", Db.Money(b.BoxPurchasePrice)), ("@bs", Db.Money(b.BoxSellingPrice)));

        /// <summary>Saves an edit to a batch's own details. Strip prices are never written — they are
        /// derived from the box prices and the strips-per-box count on read.</summary>
        public void UpdateBatch(StockBatch b)
            => _db.Execute(
                "UPDATE stock_batches SET quantity_units=@q, expiry_date=@e, batch_number=@bn, " +
                "  strips_per_box=@spb, units_per_strip=@ups, box_purchase_price=@bp, box_selling_price=@bs " +
                "WHERE id=@id",
                ("@q", b.QuantityUnits), ("@e", ExpiryParam(b.ExpiryDate)), ("@bn", Db.Text(b.BatchNumber)),
                ("@spb", b.StripsPerBox), ("@ups", b.UnitsPerStrip),
                ("@bp", Db.Money(b.BoxPurchasePrice)), ("@bs", Db.Money(b.BoxSellingPrice)), ("@id", b.Id));

        private static object ExpiryParam(DateTime? expiry)
            => expiry.HasValue
                ? (object)expiry.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : DBNull.Value;

        /// <summary>The newest batch received for an item — the one whose prices the item mirrors.
        /// Disposed batches are excluded so writing off the latest shipment falls back to the one before.</summary>
        public StockBatch GetLatestBatch(int itemId)
            => _db.QueryOne(
                $"SELECT {Cols} FROM stock_batches WHERE item_id=@i AND is_disposed=0 " +
                "ORDER BY received_at DESC, id DESC LIMIT 1", Map, ("@i", itemId));

        public void SetBatchQuantity(int batchId, int quantityUnits)
            => _db.Execute("UPDATE stock_batches SET quantity_units=@q WHERE id=@id",
                ("@q", quantityUnits), ("@id", batchId));

        /// <summary>
        /// Hard-deletes a lost/expired batch (V1.4: "after I lose the batch, delete it"). The loss value
        /// is recorded separately by <see cref="Services.InventoryService.DisposeBatch"/> as a stock
        /// adjustment (no FK on its batch_id, so it survives), and any sale allocations that referenced
        /// this batch are detached first so the enforced FK does not block the delete. Removing those
        /// allocation rows does not touch sale totals or profit — those are snapshotted on sale_lines;
        /// it only means a future return of an old sale won't restock into this (now gone) batch.
        /// Runs in one transaction so allocations and the batch never get out of sync.
        /// </summary>
        public void DisposeBatch(int batchId)
        {
            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    Exec(conn, tx, "DELETE FROM sale_line_allocations WHERE batch_id=@id", batchId);
                    Exec(conn, tx, "DELETE FROM stock_batches WHERE id=@id", batchId);
                    tx.Commit();
                }
                catch { tx.Rollback(); throw; }
            }
        }

        private static void Exec(DbConnection conn, DbTransaction tx, string sql, int id)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ("@id", id));
                cmd.ExecuteNonQuery();
            }
        }

        public void AddAdjustment(StockAdjustment a)
            => _db.Execute(
                "INSERT INTO stock_adjustments (item_id, batch_id, delta_units, reason, user_id) " +
                "VALUES (@i, @b, @d, @r, @u)",
                ("@i", a.ItemId), ("@b", Db.IntN(a.BatchId)), ("@d", a.DeltaUnits),
                ("@r", a.Reason), ("@u", a.UserId));

        public IReadOnlyDictionary<int, StockSummary> GetStockSummaries(IEnumerable<int> itemIds)
        {
            var ids = itemIds?.Distinct().ToList() ?? new List<int>();
            var result = new Dictionary<int, StockSummary>();
            if (ids.Count == 0) return result;

            var names = ids.Select((_, i) => "@p" + i).ToArray();
            var ps = ids.Select((id, i) => ("@p" + i, (object)id)).ToArray();
            // MIN over "yyyy-MM-dd" text (or DATE on MySQL) picks the nearest expiry among sellable batches.
            foreach (var row in _db.Query(
                "SELECT item_id, COALESCE(SUM(quantity_units),0) AS units, " +
                "       MIN(CASE WHEN expiry_date IS NOT NULL AND quantity_units > 0 THEN expiry_date END) AS nearest " +
                "FROM stock_batches " +
                $"WHERE is_disposed=0 AND item_id IN ({string.Join(",", names)}) GROUP BY item_id",
                r => new
                {
                    Id = Convert.ToInt32(r.GetValue(0)),
                    Units = Convert.ToInt32(r.GetValue(1)),
                    Nearest = Db.GetTimeN(r, "nearest")
                }, ps))
                result[row.Id] = new StockSummary { AvailableUnits = row.Units, NearestExpiry = row.Nearest };

            foreach (int id in ids)
                if (!result.ContainsKey(id)) result[id] = new StockSummary();
            return result;
        }

        public IReadOnlyDictionary<int, int> GetAvailableUnits(IEnumerable<int> itemIds)
        {
            var ids = itemIds?.Distinct().ToList() ?? new List<int>();
            var result = new Dictionary<int, int>();
            if (ids.Count == 0) return result;

            var names = ids.Select((_, i) => "@p" + i).ToArray();
            var ps = ids.Select((id, i) => ("@p" + i, (object)id)).ToArray();
            foreach (var kv in _db.Query(
                "SELECT item_id, COALESCE(SUM(quantity_units),0) FROM stock_batches " +
                $"WHERE is_disposed=0 AND item_id IN ({string.Join(",", names)}) GROUP BY item_id",
                r => new KeyValuePair<int, int>(Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1))), ps))
                result[kv.Key] = kv.Value;

            foreach (int id in ids) if (!result.ContainsKey(id)) result[id] = 0;
            return result;
        }

        internal static StockBatch Map(DbDataReader r) => new StockBatch
        {
            Id = Db.GetInt(r, "id"),
            ItemId = Db.GetInt(r, "item_id"),
            QuantityUnits = Db.GetInt(r, "quantity_units"),
            ExpiryDate = Db.GetTimeN(r, "expiry_date"),
            BatchNumber = Db.GetStringN(r, "batch_number"),
            StripsPerBox = Math.Max(1, Db.GetIntN(r, "strips_per_box") ?? 1),
            UnitsPerStrip = Math.Max(1, Db.GetIntN(r, "units_per_strip") ?? 1),
            // Full precision (GetRate, not GetMoney): the box price must divide back down to the exact
            // strip/unit prices the user was shown when they typed it.
            BoxPurchasePrice = Db.GetRate(r, "box_purchase_price"),
            BoxSellingPrice = Db.GetRate(r, "box_selling_price"),
            ReceivedAt = Db.GetTime(r, "received_at"),
            IsDisposed = Db.GetBool(r, "is_disposed")
        };
    }
}
