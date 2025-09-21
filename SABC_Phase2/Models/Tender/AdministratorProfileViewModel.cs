using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Tender
{
    public class AdministratorProfileViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Email address is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [Required(ErrorMessage = "First name is required")]
        [Display(Name = "First Name")]
        public string FirstName { get; set; }

        [Required(ErrorMessage = "Last name is required")]
        [Display(Name = "Last Name")]
        public string LastName { get; set; }

        // Password fields (same as OVRS)
        [DataType(DataType.Password)]
        [Display(Name = "Current Password")]
        public string? CurrentPassword { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "New Password")]
        public string? NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        [Compare("NewPassword", ErrorMessage = "New password and confirm new password do not match.")]
        public string? ConfirmPassword { get; set; }

        [Display(Name = "OTP Code")]
        public string? OtpCode { get; set; }
    }

    public class VerifyAdminOtpRequest
    {
        public string Otp { get; set; }
    }
}