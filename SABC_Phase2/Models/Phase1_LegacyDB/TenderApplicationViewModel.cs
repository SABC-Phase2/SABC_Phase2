namespace SABC_Phase2.ViewModels
{
    public class TenderApplicationViewModel
    {
        public SABC_Phase2.Models.Tender.Tender Tender { get; set; }
        public SABC_Phase2.Models.OVRS.TenderApplicationDraft Draft { get; set; }
        public int? LegacyUserId { get; set; }
        public string CompanyEmail { get; set; }
        public string CompanyName { get; set; }
    }
}