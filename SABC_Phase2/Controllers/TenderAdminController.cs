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

        private readonly TenderReportPdfService _pdfService;


        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        /// <param name="context">Database context for Tender operations.</param>
        /// <param name="configuration">App configuration settings, used for services like Azure Blob Storage.</param>
        public TenderAdminController(Phase2Context context, IConfiguration configuration, IWebHostEnvironment env, TenderReportPdfService pdfService)
        {
            _context = context;
            _configuration = configuration;
            _env = env;
            _pdfService = pdfService;
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


        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

            var dto = new TenderEditDto
            {
                Id = tender.Id,
                TenderType = tender.TenderType,
                TenderNumber = tender.TenderNumber,
                ClosingDate = tender.ClosingDate,
                ClosingTime = tender.ClosingTime,
                Status = tender.Status,
                Title = tender.Title,
                Description = tender.Description,
                ExistingDocuments = tender.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    FilePath = doc.FilePath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

            return View("Edit", dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, TenderEditDto dto)
        {
            if (!ModelState.IsValid)
            {
                return View("Edit", dto);
            }

            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

            // Update fields
            tender.TenderType = dto.TenderType;
            tender.TenderNumber = dto.TenderNumber;
            tender.ClosingDate = dto.ClosingDate.Value;
            tender.ClosingTime = dto.ClosingTime;
            tender.Status = dto.Status;
            tender.Title = dto.Title;
            tender.Description = dto.Description;

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // Handle document deletions
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var docsToRemove = tender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    // Remove from blob storage
                    await blobService.DeleteFileAsync(doc.FilePath);
                    // Remove from EF context
                    _context.TenderDocuments.Remove(doc);
                }
            }

            // Handle new PDF uploads
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
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

            await _context.SaveChangesAsync();

            return RedirectToAction("Index");
        }


        [HttpGet]
        public IActionResult ScheduledIndex(int page = 1, int pageSize = 9)
        {
            var totalItems = _context.ScheduledTenders.Count();

            var scheduledTenders = _context.ScheduledTenders
                .Include(t => t.Documents)
                .OrderByDescending(t => t.ScheduledPublishDateTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return View(scheduledTenders);
        }




        [HttpGet]
        public async Task<IActionResult> EditScheduled(int id)
        {
            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (scheduledTender == null)
                return NotFound();
            var dto = new TenderEditDto
            {
                Id = scheduledTender.Id,
                TenderType = scheduledTender.TenderType,
                TenderNumber = scheduledTender.TenderNumber,
                ClosingDate = scheduledTender.ClosingDate,
                ClosingTime = scheduledTender.ClosingTime,
                Status = scheduledTender.Status,
                Title = scheduledTender.Title,
                Description = scheduledTender.Description,
                ScheduledPublishDateTime = scheduledTender.ScheduledPublishDateTime, // <-- ADD THIS LINE
                ExistingDocuments = scheduledTender.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    FilePath = doc.FilePath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

            // Optionally: add ScheduledDate/ScheduledTime to the DTO if you want to edit them

            return View("EditScheduled", dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditScheduled(int id, TenderEditDto dto)
        {
            if (!ModelState.IsValid)
            {
                return View("EditScheduled", dto);
            }

            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (scheduledTender == null)
                return NotFound();

            // Update fields
            scheduledTender.TenderType = dto.TenderType;
            scheduledTender.TenderNumber = dto.TenderNumber;
            scheduledTender.ClosingDate = dto.ClosingDate.Value;
            scheduledTender.ClosingTime = dto.ClosingTime;
            scheduledTender.Status = dto.Status;
            scheduledTender.Title = dto.Title;
            scheduledTender.Description = dto.Description;




            // --- Add this block ---
            if (dto.IsScheduled && dto.ScheduledDate.HasValue && dto.ScheduledTime.HasValue)
            {
                var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
                var localDateTime = dto.ScheduledDate.Value.Date + dto.ScheduledTime.Value;
                scheduledTender.ScheduledPublishDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, userTimeZone);



            }

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // Handle document deletions
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var docsToRemove = scheduledTender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    await blobService.DeleteFileAsync(doc.FilePath);
                    _context.ScheduledTendersDocuments.Remove(doc);
                }
            }

            // Handle new PDF uploads
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        var blobFileName = $"{scheduledTender.Id}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        scheduledTender.Documents.Add(new ScheduledTenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName,
                            ScheduledTenderId = scheduledTender.Id
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();

            return RedirectToAction("ScheduledIndex");
        }



        //---------------------------------------------------------------------------------------------------
        //ReportsIndex()

        //---------------------------------------------------------------------------------------------------
        // Reports and Supplier Report Actions
        //---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Displays a list of all published tenders for reporting purposes.
        /// This is the entry point for the reports section, showing all tenders in the system.
        /// </summary>
        [HttpGet]
        public IActionResult Reports_Index()
        {
            // Retrieve all tenders from the database.
            // This list is passed to the view for display.
            var tenders = _context.Tenders.ToList();
            return View(tenders);
        }

        /// <summary>
        /// Displays a detailed report for a specific tender, including supplier application statistics.
        /// Shows the number of local and foreign suppliers who have applied for the selected tender.
        /// </summary>
        /// <param name="id">The unique identifier of the tender to report on.</param>
        [HttpGet]
        public IActionResult tender_Report(int id)
        {
            // Find the tender by its ID.
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);
            if (tender == null)
                return NotFound(); // Return 404 if the tender does not exist.

            // Retrieve all applications for this tender, including the related supplier (OVRS_User) data.
            var applications = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => a.TenderId == id)
                .ToList();

            // Count the number of local and foreign suppliers based on the Supplier property.
            int localCount = applications.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "local");
            int foreignCount = applications.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "foreign");

            // Create a view model containing the tender and the calculated statistics.
            var viewModel = new TenderReportViewModel
            {
                Tender = tender,
                LocalSupplierCount = localCount,
                ForeignSupplierCount = foreignCount
            };

            // Pass the view model to the view for rendering.
            return View(viewModel);
        }

        /// <summary>
        /// Generates a PDF supplier report for a specific tender.
        /// The PDF includes a list of all suppliers who applied, with their details and statistics.
        /// </summary>
        /// <param name="id">The unique identifier of the tender to generate the report for.</param>
        [HttpGet]
        public IActionResult GenerateTenderSupplierReport(int id, DateTime? startDate = null, DateTime? endDate = null)
        {
            // Find the tender by its ID.
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);
            if (tender == null)
                return NotFound(); // Return 404 if the tender does not exist.

            // Retrieve all applications for this tender, including the related supplier (OVRS_User) data.
            var query = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => a.TenderId == id);

            // Apply date range filter if dates are provided
            if (startDate.HasValue && endDate.HasValue)
            {
                // Ensure endDate includes the entire day
                var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);
                query = query.Where(a => a.DateApplied >= startDate && a.DateApplied <= endDateInclusive);
            }

            var applications = query.ToList();

            // Generate the PDF using the TenderReportPdfService.
            var pdfBytes = _pdfService.GenerateSupplierReport(tender, applications);

            // Return the PDF file as a download to the user.
            return File(pdfBytes, "application/pdf", $"SupplierReport_Tender_{tender.TenderNumber}.pdf");
        }




        [HttpGet]
        public IActionResult GenerateClosedTendersSummaryReport(DateTime? startDate, DateTime? endDate)
        {
            if (!startDate.HasValue || !endDate.HasValue)
                return BadRequest("Start and end date required");

            // Make endDate inclusive
            var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);

            var tenders = _context.Tenders
                .Where(t => t.Status != null && t.Status.ToLower().Contains("closed")
                         && t.ClosingDate >= startDate && t.ClosingDate <= endDateInclusive)
                .ToList();

            // Get all tender IDs
            var tenderIds = tenders.Select(t => t.Id).ToList();

            // Get applications for these tenders
            var allApps = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => tenderIds.Contains(a.TenderId))
                .ToList();

            var summaryList = tenders.Select(tender =>
            {
                var apps = allApps.Where(a => a.TenderId == tender.Id).ToList();
                int local = apps.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "local");
                int foreign = apps.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "foreign");
                int total = local + foreign;
                return new ClosedTenderSummaryRow
                {
                    TenderType = tender.TenderType ?? "-",
                    ClosedTenderCount = 1, // each row is 1 tender, but you could group by type if needed
                    LocalSuppliers = local,
                    ForeignSuppliers = foreign,
                    TotalApplicants = total
                };
            }).ToList();

            // If you want to group by TenderType and sum, use .GroupBy() here instead

            var pdfBytes = ClosedTendersSummaryPdfService.GenerateSummaryReport(summaryList, startDate.Value, endDate.Value);

            return File(pdfBytes, "application/pdf", $"ClosedTendersSummary_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.pdf");
        }
    }

}

