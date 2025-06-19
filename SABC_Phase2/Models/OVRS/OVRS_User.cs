using System;

namespace SABC_Phase2.Models.OVRS
{
    public class OVRS_User
    {
        public Guid Id { get; set; } = Guid.NewGuid(); // Auto-generate GUID on creation
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string CompanyName { get; set; }
        public string PhoneNumber { get; set; }
        public string EmailAddress { get; set; }
        public string Supplier { get; set; }
    }
}