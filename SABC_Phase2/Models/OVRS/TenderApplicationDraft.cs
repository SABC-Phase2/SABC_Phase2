using SABC_Phase2.Models.OVRS;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.OVRS
{
    public class TenderApplicationDraft
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public Guid DraftId { get; set; } = Guid.NewGuid();

        // User association (foreign key to Users.Id, which is int)
        public int? OVRS_UserId { get; set; }

        [ForeignKey("OVRS_UserId")]
        public OVRS_User OVRS_User { get; set; }

        // Link to Tender (optional in draft)
        public int? TenderId { get; set; }

        // Draft audit info
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime? LastModifiedDate { get; set; }

        // Collection of draft documents
        public ICollection<TenderApplicationDraftDocument> Documents { get; set; }
    }
}