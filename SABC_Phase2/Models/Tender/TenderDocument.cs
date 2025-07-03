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

        [Required]
        public string BlobName { get; set; } // <--- Add this property

        [Required]
        public string FilePath { get; set; } // Local path or Azure blob URL

        public int TenderId { get; set; }

        [ForeignKey("TenderId")]
        public Tender Tender { get; set; }
    }
}
