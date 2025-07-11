using System;

namespace SABC_Phase2.Models.OVRS
{
    public class OVRS_User
    {
        public int Id { get; set; }
        public string Role { get; set; }
        public int? LegacyUserId { get; set; } // <-- Add this line
    }
}
