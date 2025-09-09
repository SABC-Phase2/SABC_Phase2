namespace SABC_Phase2.Models.Tender
{
    public class OvrsUserViewModel
    {
        public string FullName { get; set; }
        public string Email { get; set; }
        public string CompanyName { get; set; } // You can leave this blank or fill if you have company info
        public string Role { get; set; } = "OVRS_User";
        public string Status { get; set; } = "Active";
    }
}
