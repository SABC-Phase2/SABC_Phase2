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
        //[HttpGet]
        //public IActionResult Create()
        //{
        //    return View();
        //}

        [HttpGet]
        public IActionResult Create()
        {
            return View(new TenderViewModel());
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
                    ClosingDate = model.ClosingDate.Value,
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
                ClosingDate = model.ClosingDate.Value,
                ClosingTime = model.ClosingTime,
                Status = model.Status,
                Title = model.Title,
                Description = model.Description,
                DatePublished = DateTime.UtcNow,
                DraftId = model.DraftId,
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

            // --- Remove the draft and its documents if this was from a draft ---
            if (model.DraftId.HasValue)
            {
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == model.DraftId.Value);

                if (draft != null)
                {
                    // Remove draft documents
                    if (draft.Documents != null && draft.Documents.Any())
                    {
                        _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);
                    }
                    // Remove the draft itself
                    _context.TenderAdminsDraft.Remove(draft);

                    await _context.SaveChangesAsync();
                }
            }

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDraft()
        {
            var form = Request.Form;
            var files = Request.Form.Files;

            // Try to get DraftId from the form
            Guid draftGuid;
            TenderDraft draft = null;
            if (Guid.TryParse(form["DraftId"], out draftGuid))
            {
                draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == draftGuid);
            }

            if (draft == null)
            {
                // Create new draft
                draft = new TenderDraft
                {
                    DraftId = Guid.NewGuid(),
                    CreatedDate = DateTime.UtcNow,
                    Documents = new List<TenderDraftDocument>()
                };
                _context.TenderAdminsDraft.Add(draft);
            }
            else
            {
                // Update existing draft
                draft.LastModifiedDate = DateTime.UtcNow;

                // Optionally: Remove old documents if you want to replace them
                // _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);
                // draft.Documents.Clear();
            }

            // Update fields
            draft.TenderType = form["TenderType"];
            draft.TenderNumber = form["TenderNumber"];
            draft.Status = form["Status"];
            draft.Title = form["Title"];
            draft.Description = form["Description"];

            if (DateTime.TryParse(form["ClosingDate"], out var closingDate))
                draft.ClosingDate = closingDate;
            if (TimeSpan.TryParse(form["ClosingTime"], out var closingTime))
                draft.ClosingTime = closingTime;

            // Handle file uploads (Azure Blob Storage)
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            foreach (var file in files)
            {
                if (file.Length > 0)
                {
                    var blobFileName = $"{draft.DraftId}/{Guid.NewGuid()}_{file.FileName}";
                    using var stream = file.OpenReadStream();
                    var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                    draft.Documents.Add(new TenderDraftDocument
                    {
                        FileName = file.FileName,
                        FilePath = blobFileName
                    });
                }
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Draft saved successfully.", draftId = draft.DraftId });
        }


        [HttpGet]
        public IActionResult DraftIndex()
        {
            var drafts = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .OrderByDescending(d => d.CreatedDate)
                .ToList();

            return View(drafts);
        }



        [HttpGet]
        public IActionResult EditDraft(int id)
        {
            var draft = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .FirstOrDefault(d => d.Id == id);

            if (draft == null)
                return NotFound();

            // Map draft to TenderViewModel (leave missing fields as null/default)
            var model = new TenderViewModel
            {
                DraftId = draft.DraftId, // <-- Add this line!
                TenderType = draft.TenderType,
                TenderNumber = draft.TenderNumber,
                ClosingDate = draft.ClosingDate ?? default,
                ClosingTime = draft.ClosingTime,
                Status = draft.Status,
                Title = draft.Title,
                Description = draft.Description,
                ExistingDocuments = draft.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    FilePath = doc.FilePath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

            // If ClosingDate is null, set to today to avoid validation error
            if (draft.ClosingDate == null)
                model.ClosingDate = DateTime.Today;

            return View("Create", model); // Reuse the Create view
        }




    }

}



