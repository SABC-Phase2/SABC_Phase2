using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Service responsible for publishing scheduled tenders.
    /// This is triggered either manually or via background job (e.g., Hangfire).
    /// </summary>
    public class TenderPublishingService : ITenderPublishingService
    {
        private readonly Phase2Context _context;
        private readonly BlobStorageService _blobService;

        /// <summary>
        /// Initializes the service with access to the database context and Azure Blob Storage.
        /// </summary>
        /// <param name="context">EF Core DbContext</param>
        /// <param name="configuration">App configuration to read Blob connection string</param>
        public TenderPublishingService(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            // Initialize BlobStorageService using connection string from configuration
            _blobService = new BlobStorageService(configuration["AzureBlobStorage:ConnectionString"]);
        }


        /// <summary>
        /// Main method to publish tenders that were scheduled for publishing.
        /// - Migrates from ScheduledTenders to Tenders table.
        /// - Copies associated documents in blob storage.
        /// - Removes the scheduled entry to avoid re-processing.
        /// </summary>
        public async Task PublishScheduledTendersAsync()
        {
            var now = DateTime.UtcNow;
            // Fetch all scheduled tenders that are due for publishing and still marked as "Scheduled"
            var tendersToPublish = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .Where(t => t.ScheduledPublishDateTime <= now && t.Status == "Scheduled")
                .ToListAsync();

            foreach (var scheduledTender in tendersToPublish)
            {
                // Create the main tender
                var tender = new Tender
                {
                    // 1. Create new Tender entry from ScheduledTender
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
                // Add and save Tender to generate its ID
                _context.Tenders.Add(tender);
                await _context.SaveChangesAsync(); // Save first to generate ID

                // Copy documents
                // 2. Copy each document from ScheduledTender into the TenderDocuments table
                foreach (var scheduledDoc in scheduledTender.Documents)
                {
                    // Generate new unique blob path under new Tender ID directory
                    var newBlobPath = $"{tender.Id}/{Guid.NewGuid()}_{scheduledDoc.FileName}";
                    // Copy blob file from old path to new path
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
                // 3. Remove the now-published scheduled tender to prevent re-processing
                _context.ScheduledTenders.Remove(scheduledTender);
                await _context.SaveChangesAsync();

            }
        }
    }
}