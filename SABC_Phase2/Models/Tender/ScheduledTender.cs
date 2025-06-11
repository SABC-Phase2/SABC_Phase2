using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Tender
{
    public class ScheduledTender
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string TenderType { get; set; }

        [Required]
        public string TenderNumber { get; set; }

        [Required]
        public DateTime ClosingDate { get; set; }

        public TimeSpan? ClosingTime { get; set; }

        [Required]
        public string Status { get; set; } = "Scheduled"; // Optional default status

        [Required]
        public string Title { get; set; }

        public string Description { get; set; }

        [Required]
        public DateTime ScheduledPublishDateTime { get; set; } // Used to trigger publishing

        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

        public ICollection<ScheduledTenderDocument> Documents { get; set; }
    }
}
