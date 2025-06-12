using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;

namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for administering tender creation and listing.
    /// Supports both immediate and scheduled publishing via Hangfire and Azure Blob Storage for document uploads.
    /// </summary>
    public class TenderAdminController : Controller
    {
        private readonly Phase2Context _context;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        /// <param name="context">Database context for Tender operations.</param>
        /// <param name="configuration">App configuration settings, used for services like Azure Blob Storage.</param>
        public TenderAdminController(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET: /TenderAdmin/Create
        /// <summary>
        /// GET: Render the Create Tender view.
        /// </summary>
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        // POST: /TenderAdmin/Create
        /// <summary>
        /// POST: Handles Tender creation logic.
        /// Supports both immediate and scheduled publishing.
        /// </summary>
        /// <param name="model">Tender data entered by the user.</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            // Validate the incoming model state
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            // Initialize BlobStorageService for document upload
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            // Scheduled Tender Logic
            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                // COMBINE DATE + TIME (assumed local)
                DateTime localPublishDateTime = model.ScheduledDate.Value.Date + model.ScheduledTime.Value;

                // CONVERT combined local datetime to UTC before saving
                DateTime publishDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(localPublishDateTime);
                // Create ScheduledTender entity
                var scheduledTender = new ScheduledTender
                {
                    TenderType = model.TenderType,
                    TenderNumber = model.TenderNumber,
                    ClosingDate = model.ClosingDate,
                    ClosingTime = model.ClosingTime,
                    Status = "Scheduled",
                    Title = model.Title,
                    Description = model.Description,
                    ScheduledPublishDateTime = publishDateTimeUtc,
                    CreatedOn = DateTime.UtcNow,
                    Documents = new List<ScheduledTenderDocument>()
                };

                // Upload files and associate them with the scheduled tender
                if (model.UploadedFiles != null && model.UploadedFiles.Any())
                {
                    foreach (var file in model.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            var blobFileName = $"{Guid.NewGuid()}_{file.FileName}";
                            using var stream = file.OpenReadStream();
                            var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                            scheduledTender.Documents.Add(new ScheduledTenderDocument
                            {
                                FileName = file.FileName,
                                FilePath = blobFileName
                            });
                        }
                    }
                }
                // Persist the scheduled tender to the database
                _context.ScheduledTenders.Add(scheduledTender);
                await _context.SaveChangesAsync();

                // Schedule the background job using Hangfire
                var delay = publishDateTimeUtc - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero; // Avoid negative delay

                BackgroundJob.Schedule<ITenderPublishingService>(
                    service => service.PublishScheduledTendersAsync(),
                    delay);

                return RedirectToAction("Index");
            }

            // Proceed with normal tender creation
            // Immediate Tender Logic
            var tender = new Tender
            {
                TenderType = model.TenderType,
                TenderNumber = model.TenderNumber,
                ClosingDate = model.ClosingDate,
                ClosingTime = model.ClosingTime,
                Status = model.Status,
                Title = model.Title,
                Description = model.Description,
                DatePublished = DateTime.UtcNow,
                Documents = new List<TenderDocument>()
            };
            // Upload and attach documents
            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                foreach (var file in model.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        tender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName,
                            TenderId = tender.Id
                        });
                    }
                }
            }

            _context.Tenders.Add(tender);
            await _context.SaveChangesAsync();

            return RedirectToAction("Index");
        }


        /// <summary>
        /// Displays a list of all published tenders with signed-access URIs for their documents.
        /// </summary>

        public IActionResult Index()
        {
            var tenders = _context.Tenders
                .Select(t => new Tender
                {
                    Id = t.Id,
                    TenderType = t.TenderType,
                    TenderNumber = t.TenderNumber,
                    ClosingDate = t.ClosingDate,
                    Status = t.Status,
                    Title = t.Title,
                    Description = t.Description,
                    DatePublished = t.DatePublished, // ✅ ADD THIS LINE
                    Documents = t.Documents.ToList()
                })
                .ToList();
            // Generate SAS URIs for document downloads
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            foreach (var tender in tenders)
            {
                foreach (var doc in tender.Documents)
                {
                    // Replace file path with SAS token-secured download URL
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            return View(tenders);
        }




    }
}


