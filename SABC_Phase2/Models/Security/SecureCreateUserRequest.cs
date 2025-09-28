using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Security
{
    public class SecureCreateUserRequest
    {
        [Required]
        public string FirstName { get; set; }

        [Required]
        public string LastName { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [Required]
        public string Role { get; set; }

        [Required]
        public string Password { get; set; }

        public string AzureAdId { get; set; }

        // Security fields
        [Required]
        public string DataHash { get; set; }

        [Required]
        public string Nonce { get; set; }

        [Required]
        public long Timestamp { get; set; }
    }

    public class FormTamperDetectionResult
    {
        public bool IsTampered { get; set; }
        public string TamperType { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    public class AzureUserValidationData
    {
        public string Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
    }
}