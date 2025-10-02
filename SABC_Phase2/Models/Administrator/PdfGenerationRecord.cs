namespace SABC_Phase2.Models.Administrator
{
    public class PdfGenerationRecord
    {
        public int Id { get; set; } // PK
        public string PdfType { get; set; } // "TenderSupplier", "ClosedTendersSummary", "AuditLog"
        public int SequenceNumber { get; set; } // Per-type, incremented
        public DateTime GeneratedAt { get; set; }
        public int? RelatedId { get; set; } // e.g. TenderId, if applicable
        public int? GeneratedByAdminId { get; set; }
    }
}
