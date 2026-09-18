using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteItemRepository : IItemRepository
    {
        private const string Cols =
            "id, name_en, generic_name, units_per_strip, strips_per_box, " +
            "purchase_price, selling_price, manual_price, min_quantity, max_quantity, expiry_warn_days, substitute_of, is_active, created_at, updated_at";

        private readonly IDbConnectionFactory _db;

        public SqliteItemRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Item GetById(int id)
            => _db.QueryOne($"SELECT {Cols} FROM items WHERE id=@id", Map, ("@id", id));

        public IReadOnlyList<Item> GetByIds(IEnumerable<int> ids)
        {
            var idList = ids?.Distinct().ToList() ?? new List<int>();
            if (idList.Count == 0) return new List<Item>();

            var names = idList.Select((_, i) => "@p" + i).ToArray();
            var ps = idList.Select((id, i) => ("@p" + i, (object)id)).ToArray();
            return _db.Query($"SELECT {Cols} FROM items WHERE id IN ({string.Join(",", names)})", Map, ps);
        }

        public IReadOnlyList<Item> Search(string term, bool activeOnly = true, int limit = 50)
        {
            term = (term ?? string.Empty).Trim();
            string where = activeOnly ? "is_active=1" : "1=1";
            if (string.IsNullOrEmpty(term))
                return _db.Query($"SELECT {Cols} FROM items WHERE {where} ORDER BY name_en LIMIT @lim",
                    Map, ("@lim", limit));

            // Match names OR an assigned barcode/QR code: scanning or typing a code finds the item (V1.3).
            return _db.Query(
                $"SELECT {Cols} FROM items WHERE {where} AND " +
                "(name_en LIKE @t OR generic_name LIKE @t " +
                " OR id IN (SELECT item_id FROM item_codes WHERE code LIKE @t)) ORDER BY name_en LIMIT @lim",
                Map, ("@t", "%" + term + "%"), ("@lim", limit));
        }

        public IReadOnlyList<Item> GetAll(bool activeOnly = true)
            => _db.Query($"SELECT {Cols} FROM items {(activeOnly ? "WHERE is_active=1" : "")} ORDER BY name_en", Map);

        public int Add(Item i)
            => _db.InsertId(
                "INSERT INTO items (name_en, generic_name, units_per_strip, strips_per_box, " +
                "purchase_price, selling_price, min_quantity, max_quantity, expiry_warn_days, substitute_of, is_active) VALUES " +
                "(@ne, @g, @ups, @spb, @pp, @sp, @mq, @xq, @ewd, @sub, @act)",
                ItemParams(i));

        public void Update(Item i)
        {
            var ps = ItemParams(i).Concat(new[] { ("@now", (object)Db.Time(DateTime.Now)), ("@id", (object)i.Id) }).ToArray();
            _db.Execute(
                "UPDATE items SET name_en=@ne, generic_name=@g, " +
                "units_per_strip=@ups, strips_per_box=@spb, purchase_price=@pp, selling_price=@sp, " +
                "min_quantity=@mq, max_quantity=@xq, expiry_warn_days=@ewd, substitute_of=@sub, is_active=@act, " +
                "updated_at=@now WHERE id=@id", ps);
        }

        public void SetActive(int itemId, bool active)
            => _db.Execute("UPDATE items SET is_active=@a WHERE id=@id",
                ("@a", active ? 1 : 0), ("@id", itemId));

        public bool Delete(int itemId)
        {
            // Items referenced by a sale stay for the record — refuse instead of orphaning history.
            long sales = Convert.ToInt64(_db.Scalar(
                "SELECT COUNT(*) FROM sale_lines WHERE item_id=@id", ("@id", itemId)));
            if (sales > 0) return false;

            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    foreach (string sql in new[]
                    {
                        "UPDATE items SET substitute_of=NULL WHERE substitute_of=@id",
                        "DELETE FROM item_codes WHERE item_id=@id",
                        "DELETE FROM stock_adjustments WHERE item_id=@id",
                        "DELETE FROM stock_batches WHERE item_id=@id",
                        "DELETE FROM items WHERE id=@id"
                    })
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = sql;
                            cmd.Transaction = tx;
                            DbExec.AddParams(cmd, ("@id", itemId));
                            cmd.ExecuteNonQuery();
                        }
                    tx.Commit();
                    return true;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        public void UpdateSellingPrice(int itemId, decimal newPrice)
            => _db.Execute(
                "UPDATE items SET selling_price=@p, updated_at=@now WHERE id=@id",
                ("@p", Db.Money(newPrice)), ("@now", Db.Time(DateTime.Now)), ("@id", itemId));

        public void UpdatePurchaseAndSellingPrice(int itemId, decimal purchasePerUnit, decimal sellingPerUnit)
            => _db.Execute(
                // A delivery's price is a fresh, deliberate entry, so it also lifts the hand-priced flag.
                "UPDATE items SET purchase_price=@pp, selling_price=@sp, manual_price=0, updated_at=@now WHERE id=@id",
                ("@pp", Db.Money(purchasePerUnit)), ("@sp", Db.Money(sellingPerUnit)),
                ("@now", Db.Time(DateTime.Now)), ("@id", itemId));

        /// <summary>
        /// The item's price and its batches' prices, written together or not at all (V2.3). The batch
        /// update is the same statement a delivery uses: each batch's BOX price rebuilt from the new
        /// per-unit price and its own packaging, so a batch packed 4 to a box and one packed 10 both
        /// end up selling a strip for the same money.
        /// </summary>
        public void ApplySellingPrice(int itemId, decimal sellingPerUnit, bool manual)
        {
            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE items SET selling_price=@sp, manual_price=@m, updated_at=@now WHERE id=@id";
                        cmd.Transaction = tx;
                        DbExec.AddParams(cmd, ("@sp", Db.Money(sellingPerUnit)), ("@m", manual ? 1 : 0),
                            ("@now", Db.Time(DateTime.Now)), ("@id", itemId));
                        if (cmd.ExecuteNonQuery() != 1) throw new ValidationException("الصنف غير موجود.");
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "UPDATE stock_batches SET box_selling_price = @p * strips_per_box * units_per_strip " +
                            "WHERE item_id=@id AND is_disposed=0";
                        cmd.Transaction = tx;
                        DbExec.AddParams(cmd, ("@p", Db.Money(sellingPerUnit)), ("@id", itemId));
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch { tx.Rollback(); throw; }
            }
        }

        private static (string, object)[] ItemParams(Item i) => new[]
        {
            ("@ne", (object)i.NameEn),
            ("@g", Db.Text(i.GenericName)),
            ("@ups", (object)i.UnitsPerStrip),
            ("@spb", (object)i.StripsPerBox),
            ("@pp", Db.Money(i.PurchasePrice)),
            ("@sp", Db.MoneyN(i.SellingPrice)),
            ("@mq", (object)i.MinQuantity),
            ("@xq", (object)i.MaxQuantity),
            ("@ewd", Db.IntN(i.ExpiryWarnDays)),
            ("@sub", Db.IntN(i.SubstituteOf)),
            ("@act", (object)(i.IsActive ? 1 : 0))
        };

        internal static Item Map(DbDataReader r) => new Item
        {
            Id = Db.GetInt(r, "id"),
            NameEn = Db.GetString(r, "name_en"),
            GenericName = Db.GetStringN(r, "generic_name"),
            UnitsPerStrip = Db.GetInt(r, "units_per_strip"),
            StripsPerBox = Db.GetInt(r, "strips_per_box"),
            PurchasePrice = Db.GetMoney(r, "purchase_price"),
            SellingPrice = Db.GetRateN(r, "selling_price"),   // full precision so box = perUnit*unitsPerBox is exact; NULL = not priced
            ManualPrice = (Db.GetIntN(r, "manual_price") ?? 0) != 0,
            MinQuantity = Db.GetInt(r, "min_quantity"),
            MaxQuantity = Db.GetIntN(r, "max_quantity") ?? 0,
            ExpiryWarnDays = Db.GetIntN(r, "expiry_warn_days"),
            SubstituteOf = Db.GetIntN(r, "substitute_of"),
            IsActive = Db.GetBool(r, "is_active"),
            CreatedAt = Db.GetTime(r, "created_at"),
            UpdatedAt = Db.GetTime(r, "updated_at")
        };
    }
}
