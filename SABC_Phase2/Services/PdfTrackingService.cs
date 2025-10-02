using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Administrator;

namespace SABC_Phase2.Services
{
    public interface IPdfTrackingService
    {
        Task<string> GetNextSequenceNumberAsync(string pdfType, int? relatedId = null, int? adminId = null);
        Task<PdfGenerationRecord> LogPdfGenerationAsync(string pdfType, int? relatedId = null, int? adminId = null);
        Task<int> GetTotalPdfCountAsync(string pdfType);
        Task<List<PdfGenerationRecord>> GetPdfHistoryAsync(string pdfType, int take = 100);
    }

    public class PdfTrackingService : IPdfTrackingService
    {
        private readonly Phase2Context _context;
        private readonly SouthAfricanTimeService _saTimeService;

        // Define PDF type constants
        public const string PDF_TYPE_TENDER_SUPPLIER = "TenderSupplier";
        public const string PDF_TYPE_CLOSED_TENDERS_SUMMARY = "ClosedTendersSummary";
        public const string PDF_TYPE_AUDIT_LOG = "AuditLog";

        public PdfTrackingService(Phase2Context context, SouthAfricanTimeService saTimeService)
        {
            _context = context;
            _saTimeService = saTimeService;
        }

        public async Task<string> GetNextSequenceNumberAsync(string pdfType, int? relatedId = null, int? adminId = null)
        {
            var record = await LogPdfGenerationAsync(pdfType, relatedId, adminId);
            return GenerateFormattedCode(pdfType, record.SequenceNumber);
        }

        public async Task<PdfGenerationRecord> LogPdfGenerationAsync(string pdfType, int? relatedId = null, int? adminId = null)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Get the next sequence number for this PDF type
                var lastRecord = await _context.PdfGenerationRecords
                    .Where(p => p.PdfType == pdfType)
                    .OrderByDescending(p => p.SequenceNumber)
                    .FirstOrDefaultAsync();

                int nextSequenceNumber = (lastRecord?.SequenceNumber ?? 0) + 1;

                // Create new record
                var newRecord = new PdfGenerationRecord
                {
                    PdfType = pdfType,
                    SequenceNumber = nextSequenceNumber,
                    GeneratedAt = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified(),
                    RelatedId = relatedId,
                    GeneratedByAdminId = adminId
                };

                _context.PdfGenerationRecords.Add(newRecord);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return newRecord;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<int> GetTotalPdfCountAsync(string pdfType)
        {
            return await _context.PdfGenerationRecords
                .CountAsync(p => p.PdfType == pdfType);
        }

        public async Task<List<PdfGenerationRecord>> GetPdfHistoryAsync(string pdfType, int take = 100)
        {
            return await _context.PdfGenerationRecords
                .Where(p => p.PdfType == pdfType)
                .OrderByDescending(p => p.GeneratedAt)
                .Take(take)
                .ToListAsync();
        }

        private string GenerateFormattedCode(string pdfType, int sequenceNumber)
        {
            return pdfType switch
            {
                PDF_TYPE_TENDER_SUPPLIER => $"TR{sequenceNumber:D3}", // TR001, TR002, etc.
                PDF_TYPE_CLOSED_TENDERS_SUMMARY => $"TS{sequenceNumber:D3}", // TS001, TS002, etc.
                PDF_TYPE_AUDIT_LOG => $"AL{sequenceNumber:D3}", // AL001, AL002, etc.
                _ => $"PDF{sequenceNumber:D3}" // Default format
            };
        }
    }
}