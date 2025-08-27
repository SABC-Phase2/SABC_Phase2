using System.Security.Cryptography;
using System.Text;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Provides secure password hashing utilities for the SABC Phase 2 application.
    /// This helper ensures compatibility with legacy (Phase 1) password storage using SHA256 hex encoding.
    /// </summary>
    public static class PasswordHelper
    {
        /// <summary>
        /// Hashes a plain-text password using the SHA256 algorithm and encodes the result as a lowercase hexadecimal string.
        /// 
        /// This method is compatible with the legacy Visual Basic implementation:
        /// - Input password is first encoded as UTF-8 bytes.
        /// - SHA256 is computed over the byte array.
        /// - The resulting hash is represented as a lowercase hexadecimal string, two characters per byte.
        /// 
        /// NEVER store or transmit plain-text passwords. Always use this method for password storage and authentication checks.
        /// </summary>
        /// <param name="password">The plain-text password to hash.</param>
        /// <returns>The SHA256 hash of the password as a lowercase hexadecimal string.</returns>
        public static string EncryptPassword(string password)
        {
            // Defensive: Null/empty password should throw, not hash
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty.", nameof(password));

            // Create a new SHA256 hashing instance
            using (SHA256 sha256 = SHA256.Create())
            {
                // Convert the password string to a UTF-8 byte array
                byte[] bytes = Encoding.UTF8.GetBytes(password);

                // Compute the SHA256 hash of the byte array
                byte[] hash = sha256.ComputeHash(bytes);

                // Convert the hash byte array to a lowercase hexadecimal string
                StringBuilder builder = new StringBuilder(hash.Length * 2); // Each byte -> 2 hex chars
                foreach (byte b in hash)
                {
                    // "x2": lowercase hex, 2 chars per byte (e.g. 0A => "0a")
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}