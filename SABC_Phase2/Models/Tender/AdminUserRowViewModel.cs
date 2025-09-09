namespace SABC_Phase2.Models.Tender
{
    public class AdminUserRowViewModel
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string FullName { get; set; }
        public string Role { get; set; }
        public string Status { get; set; } = "Active";
        public string CompanyName { get; set; } // <-- Add this line
    }
}