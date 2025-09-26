namespace SABC_Phase2.Models.Administrator
{
    public class PasswordHistory
    {
        public int Id { get; set; } // Primary key

        public string Email { get; set; } // User's email (unique identifier for both admin and legacy users)

        public string PasswordHash { get; set; } // Hashed password

        public DateTime ChangedAt { get; set; } // When t
    }
}
