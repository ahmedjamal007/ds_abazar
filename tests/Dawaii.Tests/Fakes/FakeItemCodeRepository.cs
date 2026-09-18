using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Tests.Fakes
{
    public class FakeItemCodeRepository : IItemCodeRepository
    {
        public readonly List<ItemCode> Codes = new List<ItemCode>();
        private int _id = 1;

        // Case-insensitive, like the real repository's UPPER() comparison.
        public ItemCode FindByCode(string code)
            => Codes.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

        public ItemCode GetByItem(int itemId) => Codes.FirstOrDefault(c => c.ItemId == itemId);

        public void SetForItem(int itemId, string code)
        {
            ItemCode owner = FindByCode(code);
            if (owner != null && owner.ItemId != itemId)
                throw new DuplicateCodeException(code, owner.ItemId);
            Codes.RemoveAll(c => c.ItemId == itemId);
            Codes.Add(new ItemCode { Id = _id++, ItemId = itemId, Code = code });
        }

        public void RemoveForItem(int itemId) => Codes.RemoveAll(c => c.ItemId == itemId);
    }
}
