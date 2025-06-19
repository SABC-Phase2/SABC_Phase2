namespace SABC_Phase2.Models.Tender
{
    /// <summary>
    /// ViewModel used to present a summary report for a specific tender,
    /// including the tender details and statistics about supplier applications.
    /// </summary>
    public class TenderReportViewModel
    {
        /// <summary>
        /// The tender for which the report is generated.
        /// Contains all details about the published tender (number, title, closing date, etc.).
        /// </summary>
        public Tender Tender { get; set; }

        /// <summary>
        /// The number of supplier applications classified as "Local" for this tender.
        /// This value is typically calculated in the controller by filtering applications.
        /// </summary>
        public int LocalSupplierCount { get; set; }

        /// <summary>
        /// The number of supplier applications classified as "Foreign" for this tender.
        /// This value is typically calculated in the controller by filtering applications.
        /// </summary>
        public int ForeignSupplierCount { get; set; }

        /// <summary>
        /// The total number of supplier applications for this tender.
        /// This is a computed property: the sum of local and foreign supplier counts.
        /// </summary>
        public int TotalResponses => LocalSupplierCount + ForeignSupplierCount;
    }
}