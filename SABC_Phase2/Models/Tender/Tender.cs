using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SABC_Phase2.Models.Tender
{
    /// <summary>
    /// Represents a published tender entry.
    /// This is the primary model shown to the public or end-users once a tender is active.
    /// </summary>
    public class Tender
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
        public string Status { get; set; }

        [Required]
        public string Title { get; set; }

        public string Description { get; set; }

        public DateTime DatePublished { get; set; } // <--- New field

        // Add this property
        public Guid? DraftId { get; set; }
        public ICollection<TenderDocument> Documents { get; set; }

        // NEW COLUMN
        public string? AwardedTender { get; set; }


    }
}


