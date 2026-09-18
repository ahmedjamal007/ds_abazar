using System;

namespace Dawaii.Core.Models
{
    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }

        /// <summary>Cached running debt: positive = the customer owes the pharmacy (see DECISIONS D-06).</summary>
        public decimal Balance { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
