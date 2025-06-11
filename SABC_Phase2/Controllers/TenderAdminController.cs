using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;

namespace SABC_Phase2.Controllers
{
    public class TenderAdminController : Controller
    {
        private readonly Phase2Context _context;
        private readonly IConfiguration _configuration;

        public TenderAdminController(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET: /TenderAdmin/Create
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        // POST: /TenderAdmin/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                // COMBINE DATE + TIME (assumed local)
                DateTime localPublishDateTime = model.ScheduledDate.Value.Date + model.ScheduledTime.Value;

                // CONVERT combined local datetime to UTC before saving
                DateTime publishDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(localPublishDateTime);

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

                // Upload and associate documents
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

                _context.ScheduledTenders.Add(scheduledTender);
                await _context.SaveChangesAsync();

                // Schedule the publishing job with UTC times
                var delay = publishDateTimeUtc - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero; // Avoid negative delay

                BackgroundJob.Schedule<ITenderPublishingService>(
                    service => service.PublishScheduledTendersAsync(),
                    delay);

                return RedirectToAction("Index");
            }

            // Proceed with normal tender creation
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

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            foreach (var tender in tenders)
            {
                foreach (var doc in tender.Documents)
                {
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            return View(tenders);
        }




    }
}


