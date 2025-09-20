namespace SABC_Phase2.Models.Tender
{
    public class AuditLog
    {
        public int Id { get; set; }
        public int AdminId { get; set; } // FK to Administrator.Id
        public string AdminEmail { get; set; }
        public string AdminFullName { get; set; } // <--- Add this line
        public string ActionType { get; set; } // e.g. "CreateTender", "EditTender", etc.
        public string Description { get; set; } // More details (Tender #, User affected, etc.)
        public DateTime Timestamp { get; set; }
    }
}