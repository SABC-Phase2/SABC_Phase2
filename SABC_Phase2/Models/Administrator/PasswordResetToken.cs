namespace SABC_Phase2.Models.Administrator
{
    public class PasswordResetToken
    {

        public int Id { get; set; }
        public string Email { get; set; }
        public string Token { get; set; }
        public DateTime ExpiryDate { get; set; }
        public string UserType { get; set; } // "Admin" or "OvrUser"
    }
}
