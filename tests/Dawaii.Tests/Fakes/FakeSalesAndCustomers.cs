using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.Tests.Fakes
{
    public class FakeCustomerRepository : ICustomerRepository
    {
        public readonly List<Customer> Customers = new List<Customer>();
        public readonly List<DebtTransaction> Transactions = new List<DebtTransaction>();
        private int _id = 1, _txId = 1;

        public Customer GetById(int id) => Customers.FirstOrDefault(c => c.Id == id);
        public IReadOnlyList<Customer> Search(string term, int limit = 50)
            => Customers.Where(c => string.IsNullOrEmpty(term) || (c.Name ?? "").Contains(term)).Take(limit).ToList();
        public IReadOnlyList<Customer> GetAll() => Customers.ToList();
        public IReadOnlyList<Customer> GetWithDebt() => Customers.Where(c => c.Balance != 0).ToList();
        public int Add(Customer c) { c.Id = _id++; Customers.Add(c); return c.Id; }
        public void Update(Customer c)
        {
            var e = GetById(c.Id);
            if (e != null) { e.Name = c.Name; e.Phone = c.Phone; }
        }
        public bool Delete(int customerId)
        {
            // Mirrors the real rule: a customer named on any ledger entry stays.
            if (Transactions.Any(t => t.CustomerId == customerId)) return false;
            return Customers.RemoveAll(c => c.Id == customerId) > 0;
        }

        public IReadOnlyList<DebtTransaction> GetTransactions(int customerId)
            => Transactions.Where(t => t.CustomerId == customerId).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();

        // Test helper mirroring the atomic charge/payment done in the DB layer.
        public DebtTransaction Apply(int customerId, DebtTransactionType type, decimal amount, int? saleId, int userId, string note)
        {
            var c = GetById(customerId) ?? throw new ValidationException("no customer");
            var tx = new DebtTransaction
            {
                Id = _txId++, CustomerId = customerId, Type = type, Amount = amount,
                SaleId = saleId, UserId = userId, Note = note, CreatedAt = DateTime.Now
            };
            Transactions.Add(tx);
            c.Balance += tx.SignedAmount;
            return tx;
        }
    }

    /// <summary>
    /// In-memory <see cref="ISaleStore"/> that mirrors the MySQL store's effects (stock decrement with
    /// guard, debt charge, return restore) so POS flows can be tested without a database.
    /// </summary>
    public class FakeSaleStore : ISaleStore
    {
        private readonly FakeStockRepository _stock;
        private readonly FakeCustomerRepository _customers;
        public readonly List<Sale> Sales = new List<Sale>();
        public readonly List<Return> Returns = new List<Return>();
        private int _id = 1, _lineId = 1;

        public FakeSaleStore(FakeStockRepository stock, FakeCustomerRepository customers = null)
        {
            _stock = stock;
            _customers = customers;
        }

        public Sale Save(Sale sale)
        {
            // Apply guarded decrements just like the DB store.
            foreach (var line in sale.Lines)
                foreach (var a in line.Allocations)
                {
                    var b = _stock.GetBatch(a.BatchId);
                    if (b == null || b.IsDisposed || b.QuantityUnits < a.Units)
                        throw new InsufficientStockException(line.ItemId, a.Units, b?.QuantityUnits ?? 0);
                    _stock.SetBatchQuantity(a.BatchId, b.QuantityUnits - a.Units);
                }

            sale.Id = _id++;
            sale.SaleNumber = sale.Id;
            // The DB hands every line an id; returns address lines by it, so the fake must too.
            foreach (var line in sale.Lines) { line.Id = _lineId++; line.SaleId = sale.Id; }
            if (sale.SaleType == SaleType.Credit && sale.CustomerId.HasValue && _customers != null)
                _customers.Apply(sale.CustomerId.Value, DebtTransactionType.Charge, sale.Total, sale.Id, sale.UserId, "بيع آجل");
            Sales.Add(sale);
            return sale;
        }

        public Sale GetById(int id) => Sales.FirstOrDefault(s => s.Id == id);

        public Sale GetBySaleNumber(int saleNumber)
            => saleNumber <= 0 ? null : Sales.FirstOrDefault(s => s.SaleNumber == saleNumber);

        public IReadOnlyList<Sale> GetByDateRange(DateTime fromInclusive, DateTime toExclusive)
            => Sales.Where(s => s.CreatedAt >= fromInclusive && s.CreatedAt < toExclusive).ToList();

        public Return ReturnSale(int saleId, int userId, string reason)
            => Refund(saleId, userId, reason, null);

        public Return ReturnItems(int saleId, int userId, string reason, IList<ReturnRequest> requests)
        {
            if (requests == null || requests.Count == 0) throw new ValidationException("no quantities");
            return Refund(saleId, userId, reason, requests);
        }

        /// <summary>Mirrors the DB store: priced by the same calculator, units back on the batches they
        /// came off, invoice totals and status updated, debt reversed by the refunded amount.</summary>
        private Return Refund(int saleId, int userId, string reason, IList<ReturnRequest> requests)
        {
            var sale = GetById(saleId) ?? throw new ValidationException("no sale");
            ReturnPlan plan = ReturnCalculator.Plan(sale, requests ?? ReturnCalculator.EverythingOutstanding(sale));

            foreach (ReturnLine rl in plan.Lines)
            {
                SaleLine line = sale.Lines.First(l => l.Id == rl.SaleLineId);
                RestoreUnits(line, rl.Units);
                line.ReturnedUnits += rl.Units;
            }

            sale.ReturnedTotal += plan.Total;
            sale.ReturnedCost += plan.Cost;
            sale.Status = plan.CompletesSale ? SaleStatus.Returned : SaleStatus.PartiallyReturned;

            if (sale.SaleType == SaleType.Credit && sale.CustomerId.HasValue && _customers != null && plan.Total != 0m)
                _customers.Apply(sale.CustomerId.Value, DebtTransactionType.Payment, plan.Total, saleId, userId, "إرجاع فاتورة");

            var ret = new Return
            {
                Id = Returns.Count + 1, SaleId = saleId, UserId = userId, Reason = reason,
                Total = plan.Total, Cost = plan.Cost, CompletedTheSale = plan.CompletesSale,
                CreatedAt = DateTime.Now, Lines = plan.Lines
            };
            Returns.Add(ret);
            return ret;
        }

        /// <summary>Walks the line's allocations in order, skipping units earlier returns already gave back.</summary>
        private void RestoreUnits(SaleLine line, int units)
        {
            int skip = line.ReturnedUnits, outstanding = units;
            foreach (SaleLineAllocation a in line.Allocations)
            {
                if (outstanding == 0) break;
                int available = a.Units;
                if (skip > 0)
                {
                    int skipped = Math.Min(skip, available);
                    skip -= skipped;
                    available -= skipped;
                }
                if (available <= 0) continue;

                int give = Math.Min(available, outstanding);
                var batch = _stock.GetBatch(a.BatchId);
                if (batch != null) _stock.SetBatchQuantity(a.BatchId, batch.QuantityUnits + give);
                outstanding -= give;
            }
        }
    }
}
