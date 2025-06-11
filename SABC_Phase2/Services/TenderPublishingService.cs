using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;

namespace SABC_Phase2.Services
{
    public class TenderPublishingService : ITenderPublishingService
    {
        private readonly Phase2Context _context;
        private readonly BlobStorageService _blobService;

        public TenderPublishingService(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            _blobService = new BlobStorageService(configuration["AzureBlobStorage:ConnectionString"]);
        }

        public async Task PublishScheduledTendersAsync()
        {
            var now = DateTime.UtcNow;
            var tendersToPublish = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .Where(t => t.ScheduledPublishDateTime <= now && t.Status == "Scheduled")
                .ToListAsync();

            foreach (var scheduledTender in tendersToPublish)
            {
                // Create the main tender
                var tender = new Tender
                {
                    TenderType = scheduledTender.TenderType,
                    TenderNumber = scheduledTender.TenderNumber,
                    ClosingDate = scheduledTender.ClosingDate,
                    ClosingTime = scheduledTender.ClosingTime,
                    Status = "Open Tender",
                    Title = scheduledTender.Title,
                    Description = scheduledTender.Description,
                    DatePublished = DateTime.UtcNow,
                    Documents = new List<TenderDocument>()
                };

                _context.Tenders.Add(tender);
                await _context.SaveChangesAsync(); // Save first to generate ID

                // Copy documents
                foreach (var scheduledDoc in scheduledTender.Documents)
                {
                    var newBlobPath = $"{tender.Id}/{Guid.NewGuid()}_{scheduledDoc.FileName}";
                    await _blobService.CopyBlobAsync(scheduledDoc.FilePath, newBlobPath);

                    var tenderDoc = new TenderDocument
                    {
                        FileName = scheduledDoc.FileName,
                        FilePath = newBlobPath,
                        TenderId = tender.Id
                    };

                    _context.TenderDocuments.Add(tenderDoc);
                }

                // Save the documents explicitly
                await _context.SaveChangesAsync();

                // Remove the scheduled tender
                _context.ScheduledTenders.Remove(scheduledTender);
                await _context.SaveChangesAsync();

            }
        }
    }
}