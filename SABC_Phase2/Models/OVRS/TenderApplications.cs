using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SABC_Phase2.Models.Tender;

namespace SABC_Phase2.Models.OVRS
{
    public class TenderApplications
    {
        [Key]
        public int Id { get; set; }

        // Foreign key to Tender
        [Required]
        public int TenderId { get; set; }
        [ForeignKey("TenderId")]
        public Tender.Tender Tender { get; set; }

        // Foreign key to OVRS_User
        [Required]
        public Guid OVRS_UserId { get; set; }
        [ForeignKey("OVRS_UserId")]
        public OVRS_User OVRS_User { get; set; }

        public DateTime? DateApplied { get; set; } // this neeeds to automatically populate on date user created tender application


        // Exposed fields from Tender
        [NotMapped]
        public string TenderType => Tender?.TenderType;
        [NotMapped]
        public string TenderTitle => Tender?.Title;
        [NotMapped]
        public string TenderNumber => Tender?.TenderNumber;
        [NotMapped]
        public DateTime? ClosingDate => Tender?.ClosingDate;
        [NotMapped]
        public TimeSpan? ClosingTime => Tender?.ClosingTime;

        // Exposed fields from OVRS_User
        [NotMapped]
        public string CompanyEmail => OVRS_User?.EmailAddress;
        [NotMapped]
        public string CompanyName => OVRS_User?.CompanyName;
    }
}