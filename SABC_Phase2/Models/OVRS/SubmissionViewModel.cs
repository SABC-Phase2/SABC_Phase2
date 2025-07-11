namespace SABC_Phase2.Models.OVRS
{
    public class SubmissionViewModel
    {
        public int ApplicationId { get; set; } // Add this!
        public string TenderNumber { get; set; }
        public string DateSubmitted { get; set; }
        public string TimeSubmitted { get; set; }
        public string Status { get; set; }
        public string ClosingDateTime { get; set; }
        public int TenderId { get; set; }
    }
}
