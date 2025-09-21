namespace SABC_Phase2.Models.Administrator
{
    public class Administrator
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; } // Store hashed passwords for security
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Role { get; set; } = "Administrator";

        // ✅ New column: 1 = Active, 0 = Deleted
        public int AccountStatus { get; set; }

        // OTP functionality fields (existing)
        public string? OtpCode { get; set; }
        public DateTime? OtpExpiration { get; set; }
        public string? PendingEmail { get; set; }
        public string? OtpType { get; set; }
        public DateTime? LastOtpRequestTime { get; set; }

        // Add password last updated tracking
        public DateTime? PasswordLastUpdated { get; set; }
    }
}