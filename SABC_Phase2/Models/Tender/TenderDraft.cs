using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Tender
{
    public class TenderDraft
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public Guid DraftId { get; set; } = Guid.NewGuid(); // Unique draft identifier

        // public string UserId { get; set; } // To associate drafts with users
        public DateTime CreatedDate { get; set; }
        public DateTime? LastModifiedDate { get; set; }

        // All the same fields as Tender but not required
        public string TenderType { get; set; }
        public string TenderNumber { get; set; }
        public DateTime? ClosingDate { get; set; }
        public TimeSpan? ClosingTime { get; set; }
        public string Status { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        public ICollection<TenderDraftDocument> Documents { get; set; }
    }
}