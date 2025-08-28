using SABC_Phase2.Services;
using System.ComponentModel.DataAnnotations;

// Profile view model
namespace SABC_Phase2.Models.OVRS
{
    public class OVRS_UserProfileViewModel
    {
        [Required(ErrorMessage = "First name is required")]
        [Display(Name = "First Name")]
        public string FirstName { get; set; }

        [Display(Name = "Middle Name")]
        public string? MiddleName { get; set; }

        [Required(ErrorMessage = "Last name is required")]
        [Display(Name = "Last Name")]
        public string LastName { get; set; }

        [Display(Name = "Company Name")]
        public string? CompanyName { get; set; }

        [Display(Name = "Display as Company")]
        public bool DisplayAsCompany { get; set; }

        [Required(ErrorMessage = "Country code is required")]
        [Display(Name = "Country Code")]
        public string CountryCode { get; set; } = "+27";

        [Required(ErrorMessage = "Phone number is required")]
        [Phone(ErrorMessage = "Invalid phone number format")]
        [Display(Name = "Phone Number")]
        public string PhoneNumber { get; set; }

        [Required(ErrorMessage = "Email address is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Current Password")]
        public string? CurrentPassword { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "New Password")]
        public string? NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        [Compare("NewPassword", ErrorMessage = "Passwords do not match.")]
        public string? ConfirmPassword { get; set; }

        [Display(Name = "Last Password Update")]
        public DateTime? PasswordLastUpdated { get; set; }

        [Display(Name = "OTP Code")]
        public string? OtpCode { get; set; }

        public List<CountryCode> CountryCodes { get; set; } = new List<CountryCode>();
    }
}