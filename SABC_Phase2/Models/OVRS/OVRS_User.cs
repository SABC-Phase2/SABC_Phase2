using System;

namespace SABC_Phase2.Models.OVRS
{
    public class OVRS_User
    {
        public int Id { get; set; }
        public string Role { get; set; }
        public int? LegacyUserId { get; set; }

        // New columns for OTP
        public string? OtpCode { get; set; } // 6 digit OTP, keep as string for leading zeros
        public DateTime? OtpExpiration { get; set; } // When the OTP expires
        public string? PendingPhoneNumber { get; set; } // Store the new phone number temporarily
        public string? PendingCountryCode { get; set; } // Store the new country code temporarily
    }
}