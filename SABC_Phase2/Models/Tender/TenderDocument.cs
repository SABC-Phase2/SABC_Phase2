using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Tender
{

    public class TenderDocument
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string FileName { get; set; }

        // Remove BlobName, add SharePointPath instead
        [Required]
        public string SharePointPath { get; set; }

        // Foreign key to Tender (always set)
        public int TenderId { get; set; }
        [ForeignKey("TenderId")]
        public Tender Tender { get; set; }

        // Foreign key to AwardedTender (set only if document is for awarded tender)
        public int? AwardedTenderId { get; set; }
        [ForeignKey("AwardedTenderId")]
        public AwardedTender AwardedTender { get; set; }
    }
}