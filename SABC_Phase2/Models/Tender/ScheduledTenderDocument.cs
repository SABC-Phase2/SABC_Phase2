using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Tender
{
    /// <summary>
    /// Represents a document associated with a scheduled tender.
    /// These documents are uploaded before the tender is officially published.
    /// Once published, they are copied to TenderDocuments under a new Tender record.
    /// </summary>
    public class ScheduledTenderDocument
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string FileName { get; set; }

        [Required]
        public string FilePath { get; set; } // Azure Blob Storage URL or path

        public int ScheduledTenderId { get; set; }

        [ForeignKey("ScheduledTenderId")]
        public ScheduledTender ScheduledTender { get; set; }
    }
}
