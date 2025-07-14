using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Phase1_LegacyDB
{
    // Maps to the legacy supplier table in the old Phase 1 database.
    // Used for resolving company name and supplier type for reporting.
    [Table("tbl_suppliers")]
    public class TblSuppliers
    {
        // Primary key: legacy user ID
        [Key]
        [Column("user_id")]
        public int UserId { get; set; }

        // Legal registered name of the supplier company
        [Column("legalname")]
        public string LegalName { get; set; }

        // Trading name of the supplier company (preferred for display if available)
        [Column("tradingname")]
        public string TradingName { get; set; }

        // Supplier's email address (from legacy DB)
        [Column("email")]
        public string Email { get; set; }

        // Supplier type code: 1 = Local, 2 = Foreign
        [Column("local/foreigner")]
        public int LocalForeigner { get; set; }
    }
}