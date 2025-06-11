using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Tender
{
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
