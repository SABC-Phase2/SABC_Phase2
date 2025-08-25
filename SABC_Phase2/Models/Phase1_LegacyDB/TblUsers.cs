using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Phase1_LegacyDB
{
    [Table("tbl_users")]
    public class TblUsers
    {
        [Key]
        [Column("user_id")]
        public int UserId { get; set; }

        [Column("first_name")]
        public string? FirstName { get; set; }

        [Column("last_name")]
        public string? LastName { get; set; }

        [Column("email")]
        public string? Email { get; set; }

        [Column("phone")]
        public string? Phone { get; set; }

        [Column("password")]
        public string? Password { get; set; } // <-- Add this

        [Column("updated_date")]
        public DateTime? UpdatedDate { get; set; } // <-- Add this
    }
}