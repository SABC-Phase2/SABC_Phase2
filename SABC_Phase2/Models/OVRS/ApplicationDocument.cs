using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.OVRS
{
    public class ApplicationDocument
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string FileName { get; set; }

        [Required]
        public string BlobName { get; set; }

        [Required]
        public string FilePath { get; set; }

        public int TenderApplicationId { get; set; }

        [ForeignKey("TenderApplicationId")]
        public TenderApplications TenderApplication { get; set; }
    }
}