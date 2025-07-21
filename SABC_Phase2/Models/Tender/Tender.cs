using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

        // Foreign key to AwardedTender, nullable
        public int? AwardedTenderId { get; set; }
        [ForeignKey("AwardedTenderId")]
        public AwardedTender AwardedTender { get; set; }


    }
}


