namespace SABC_Phase2.Models.Tender
{
    public class ClosedTenderSummaryRow
    {
        public string TenderType { get; set; }
        public int ClosedTenderCount { get; set; }
        public int LocalSuppliers { get; set; }
        public int ForeignSuppliers { get; set; }
        public int TotalApplicants { get; set; }
    }
}