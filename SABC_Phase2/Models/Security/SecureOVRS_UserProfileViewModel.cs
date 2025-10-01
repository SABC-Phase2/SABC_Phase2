using SABC_Phase2.Models.OVRS;
using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Security
{
    public class SecureOVRS_UserProfileViewModel : OVRS_UserProfileViewModel
    {
        // Security fields
        [Required]
        public string DataHash { get; set; }

        [Required]
        public string Nonce { get; set; }

        [Required]
        public long Timestamp { get; set; }
    }

    public class ProfileSecurityData
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string CompanyName { get; set; }
        public string PhoneNumber { get; set; }
        public string CountryCode { get; set; }
    }
}