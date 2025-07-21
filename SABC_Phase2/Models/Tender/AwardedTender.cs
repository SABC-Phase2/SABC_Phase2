using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Tender
{
    public class AwardedTender
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string AwardedCompanyName { get; set; }

        public virtual ICollection<TenderDocument> Documents { get; set; } = new List<TenderDocument>();

        public int TenderId { get; set; }
        [ForeignKey("TenderId")]
        public virtual Tender Tender { get; set; }
    }
}