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
    }
}
