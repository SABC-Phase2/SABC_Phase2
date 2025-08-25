using System;
using System.Security.Cryptography;

namespace SABC_Phase2.Services
{
    public class OtpService
    {
        /// <summary>
        /// Generates a 6-digit OTP code
        /// </summary>
        /// <returns>6-digit string OTP</returns>
        public string GenerateOtpCode()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] bytes = new byte[4];
                rng.GetBytes(bytes);

                // Convert to positive integer and ensure it's 6 digits
                int value = Math.Abs(BitConverter.ToInt32(bytes, 0));
                return (value % 1000000).ToString("D6");
            }
        }

        /// <summary>
        /// Gets the OTP expiration time (5 minutes from now)
        /// </summary>
        /// <returns>DateTime representing expiration time</returns>
        public DateTime GetOtpExpiration()
        {
            return DateTime.UtcNow.AddMinutes(5);
        }

        /// <summary>
        /// Checks if an OTP is valid and not expired
        /// </summary>
        /// <param name="storedOtp">OTP stored in database</param>
        /// <param name="storedExpiration">Expiration time from database</param>
        /// <param name="providedOtp">OTP provided by user</param>
        /// <returns>True if valid and not expired</returns>
        public bool ValidateOtp(string storedOtp, DateTime? storedExpiration, string providedOtp)
        {
            if (string.IsNullOrEmpty(storedOtp) || string.IsNullOrEmpty(providedOtp))
                return false;

            if (storedExpiration == null || DateTime.UtcNow > storedExpiration.Value)
                return false;

            return storedOtp == providedOtp;
        }
    }
}