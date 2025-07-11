namespace SABC_Phase2.Models.OVRS
{
    public class DraftTileViewModel
    {
        public Guid DraftId { get; set; }
        public int TenderId { get; set; }
        public string TenderNumber { get; set; } // <-- Add this line!
        public string TenderTitle { get; set; }
        public DateTime? ClosingDate { get; set; }
        public TimeSpan? ClosingTime { get; set; }
        public string Status { get; set; }
    }
}
