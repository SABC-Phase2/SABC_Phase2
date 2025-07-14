// This model is used to transfer supplier information to the PDF report generation logic.
// It represents a supplier (user) who has applied for a tender, including relevant legacy info.
namespace SABC_Phase2.Models.Tender
{
    public class OVRS_UserPdfInfoViewModel
    {
        // Database ID of the OVRS user in the new system
        public int Id { get; set; }



        // Legacy supplier/user ID from the Phase 1 (legacy) database
        public int LegacyUserId { get; set; }



        // Supplier company name (usually comes from legacy DB, TradingName or LegalName)
        public string CompanyName { get; set; }



        // Supplier type: "Local" or "Foreign" (resolved from legacy DB code)
        public string SupplierType { get; set; }
    }
}