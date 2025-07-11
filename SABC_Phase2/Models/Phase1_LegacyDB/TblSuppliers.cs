using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Phase1_LegacyDB
{
    [Table("tbl_suppliers")]

    public class TblSuppliers
    {
        [Key]
        [Column("user_id")]
        public int UserId { get; set; }

        [Column("legalname")]
        public string LegalName { get; set; }

        [Column("email")]
        public string Email { get; set; }
    }
}