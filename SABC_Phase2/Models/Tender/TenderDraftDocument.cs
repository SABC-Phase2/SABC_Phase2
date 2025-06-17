using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Tender
{
    public class TenderDraftDocument
    {
        [Key]
        public int Id { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; } // Azure blob URL
        public int TenderDraftId { get; set; }

        [ForeignKey("TenderDraftId")]
        public TenderDraft TenderDraft { get; set; }
    }
}
