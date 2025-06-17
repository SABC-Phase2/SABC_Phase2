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
        private readonly IWebHostEnvironment _env;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        /// <param name="context">Database context for Tender operations.</param>
        /// <param name="configuration">App configuration settings, used for services like Azure Blob Storage.</param>
        public TenderAdminController(Phase2Context context, IConfiguration configuration, IWebHostEnvironment env)
        {
            _context = context;
            _configuration = configuration;
            _env = env;
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

        public IActionResult Index(int page = 1, int pageSize = 9)
        {
            var totalItems = _context.Tenders.Count();

            var tenders = _context.Tenders
                .Include(t => t.Documents)
                .OrderByDescending(t => t.DatePublished)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            foreach (var tender in tenders)
            {
                foreach (var doc in tender.Documents)
                {
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return View(tenders);
        }
        // GET: TenderAdmin/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var tender = _context.Tenders
                .Where(t => t.Id == id)
                .Select(t => new TenderViewModel
                {
                    Id = t.Id,
                    TenderType = t.TenderType,
                    TenderNumber = t.TenderNumber,
                    ClosingDate = t.ClosingDate,
                    ClosingTime = t.ClosingTime,
                    Status = t.Status,
                    Title = t.Title,
                    Description = t.Description,
                    ExistingDocuments = t.Documents.Select(d => new TenderDocumentViewModel
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        FilePath = d.FilePath
                    }).ToList()
                })
                .FirstOrDefault();

            if (tender == null)
                return NotFound();

            return View(tender);
        }

       [HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Edit(
    TenderViewModel model,
    List<IFormFile> UploadedFiles,
    [FromForm] List<int> DocumentsToDelete)
{
    if (!ModelState.IsValid)
    {
        // Repopulate ExistingDocuments if needed
        var tenderDocs = _context.TenderDocuments
            .Where(d => d.TenderId == model.Id)
            .Select(d => new TenderDocumentViewModel
            {
                Id = d.Id,
                FileName = d.FileName,
                FilePath = d.FilePath
            }).ToList();
        model.ExistingDocuments = tenderDocs;
        return View(model);
    }

    var tender = _context.Tenders
        .Include(t => t.Documents)
        .FirstOrDefault(t => t.Id == model.Id);

    if (tender == null)
        return NotFound();

    // Update tender fields
    tender.TenderType = model.TenderType;
    tender.TenderNumber = model.TenderNumber;
    tender.ClosingDate = model.ClosingDate;
    tender.ClosingTime = model.ClosingTime;
    tender.Status = model.Status;
    tender.Title = model.Title;
    tender.Description = model.Description;

    // Handle document deletions
    if (DocumentsToDelete != null && DocumentsToDelete.Any())
    {
        var docsToRemove = tender.Documents.Where(d => DocumentsToDelete.Contains(d.Id)).ToList();
        foreach (var doc in docsToRemove)
        {
            // Optionally: Delete the file from disk
            var filePath = Path.Combine(_env.WebRootPath, doc.FilePath.TrimStart('~', '/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);

            _context.TenderDocuments.Remove(doc);
        }
    }

    // Handle new uploads
    if (UploadedFiles != null && UploadedFiles.Any())
    {
        foreach (var file in UploadedFiles)
        {
            if (file.Length > 0)
            {
                var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads/tenderdocs");
                Directory.CreateDirectory(uploadsFolder);
                var uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var doc = new TenderDocument
                {
                    FileName = file.FileName,
                    FilePath = $"/uploads/tenderdocs/{uniqueFileName}",
                    TenderId = tender.Id
                };
                _context.TenderDocuments.Add(doc);
            }
        }
    }

    await _context.SaveChangesAsync();
    return RedirectToAction("Index");
}

    }

}



