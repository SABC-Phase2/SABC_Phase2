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
        private readonly SharePointService _sharePointService;

        /// <summary>
        /// Initializes the service with access to the database context and SharePoint service.
        /// </summary>
        /// <param name="context">EF Core DbContext</param>
        /// <param name="configuration">App configuration to read SharePoint config</param>
        public TenderPublishingService(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            // Initialize SharePointService (replace BlobStorageService)
            _sharePointService = new SharePointService(configuration);
        }


        /// <summary>
        /// Main method to publish tenders that were scheduled for publishing.
        /// - Migrates from ScheduledTenders to Tenders table.
        /// - Copies associated documents in SharePoint.
        /// - Removes the scheduled entry to avoid re-processing.
        /// </summary>
        public async Task PublishScheduledTendersAsync()
        {
            var now = DateTime.UtcNow;
            // Fetch all scheduled tenders due for publishing
            var tendersToPublish = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .Where(t => t.ScheduledPublishDateTime <= now && t.Status == "Scheduled")
                .ToListAsync();

            foreach (var scheduledTender in tendersToPublish)
            {
                // Check for duplicate TenderNumber
                var existingTender = await _context.Tenders
                    .FirstOrDefaultAsync(t => t.TenderNumber == scheduledTender.TenderNumber);

                if (existingTender != null)
                {
                    // Already published: just clean up the scheduled tender
                    _context.ScheduledTenders.Remove(scheduledTender);
                    await _context.SaveChangesAsync();
                    continue;
                }

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
                    DatePublished = scheduledTender.ScheduledPublishDateTime,
                    Documents = new List<TenderDocument>()
                };

                _context.Tenders.Add(tender);
                await _context.SaveChangesAsync(); // Save first to generate ID

                // Copy documents from ScheduledTenderDocuments to TenderDocuments and upload to SharePoint
                foreach (var scheduledDoc in scheduledTender.Documents)
                {
                    // Download the file from its original FilePath (Blob or SharePoint), upload to new SharePoint path
                    // You may need to implement a helper in SharePointService to copy from old to new if not already present
                    // For now, assume FilePath is a URL or local path that can be streamed

                    // Replace this with a migration to SharePoint (if not already there)
                    string sharePointUrl;
                    using (var fileStream = await _sharePointService.GetFileStreamAsync(scheduledDoc.SharePointPath)) // You must implement this method if needed
                    {
                        sharePointUrl = await _sharePointService.UploadDocumentAsync(
                            scheduledTender.TenderNumber,
                            fileStream,
                            scheduledDoc.FileName);
                    }

                    var tenderDoc = new TenderDocument
                    {
                        FileName = scheduledDoc.FileName,
                        SharePointPath = sharePointUrl,
                        TenderId = tender.Id
                    };

                    _context.TenderDocuments.Add(tenderDoc);
                }

                await _context.SaveChangesAsync();

                // Remove the scheduled tender
                _context.ScheduledTenders.Remove(scheduledTender);
                await _context.SaveChangesAsync();
            }
        }
    }
}