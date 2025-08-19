using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.Phase1_LegacyDB
{
    [Table("tbl_users")]
    [Keyless]
    public class TblUsers
    {
        [Column("user_id")]
        public int UserId { get; set; }

        [Column("first_name")]
        public string? FirstName { get; set; }  // Nullable!

        [Column("last_name")]
        public string? LastName { get; set; }   // Nullable!

        [Column("email")]
        public string? Email { get; set; }

        [Column("phone")]
        public string? Phone { get; set; }
    }
}