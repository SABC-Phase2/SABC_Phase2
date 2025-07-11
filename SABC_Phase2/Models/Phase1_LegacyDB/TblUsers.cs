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
        public string FirstName { get; set; }

        [Column("last_name")]
        public string LastName { get; set; }

        [Column("email")]
        public string Email { get; set; }

        // Add any more columns from tbl_users as needed!
    }
}