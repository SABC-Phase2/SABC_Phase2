using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models;
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
        // Dependency-injected database context for EF Core operations.
        private readonly Phase2Context _context;
        private readonly LegacyDbContext _legacyContext;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly SouthAfricanTimeService _saTimeService;
        private readonly TenderReportPdfService _pdfService;


        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        public TenderAdminController( Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration,IWebHostEnvironment env, TenderReportPdfService pdfService, SouthAfricanTimeService saTimeService)
        {
            // Assign the injected database context to a private field for use throughout the controller.
            // This context enables database operations such as querying and saving tenders.
            _context = context;
            _legacyContext = legacyContext;

            // Assign the injected configuration object to a private field.
            // This allows access to application settings (e.g., connection strings, custom config values).
            _configuration = configuration;

            // Assign the injected web host environment to a private field.
            // Useful for determining the current environment (Development, Production, etc.) 
            // and accessing environment-specific paths or settings.
            _env = env;

            // Assign the injected PDF service to a private field.
            // This service is used to generate tender report PDFs as needed.
            _pdfService = pdfService;

            //(NodaTime Time API))
            _saTimeService = saTimeService;
        }



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
        /// 
        [Authorize(Roles = "Administrator")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            if (!User.Identity.IsAuthenticated || !User.IsInRole("Administrator"))
                return Forbid();

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // --- Scheduled Tender Logic ---
            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                // Combine the scheduled date and time as South African local time
                var scheduledLocal = model.ScheduledDate.Value.Date + model.ScheduledTime.Value;
                var scheduledSaLocal = new NodaTime.LocalDateTime(
                    scheduledLocal.Year, scheduledLocal.Month, scheduledLocal.Day,
                    scheduledLocal.Hour, scheduledLocal.Minute, scheduledLocal.Second
                );
                // Convert South African local time to UTC
                var scheduledUtcInstant = _saTimeService.ConvertSaLocalToUtc(scheduledSaLocal);
                var scheduledUtc = scheduledUtcInstant.ToDateTimeUtc();

                var scheduledTender = new ScheduledTender
                {
                    TenderType = model.TenderType,
                    TenderNumber = model.TenderNumber,
                    ClosingDate = model.ClosingDate.Value,
                    ClosingTime = model.ClosingTime,
                    Status = "Scheduled",
                    Title = model.Title,
                    Description = model.Description,
                    ScheduledPublishDateTime = scheduledUtc,
                    CreatedOn = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified(),
                    Documents = new List<ScheduledTenderDocument>()
                };

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

                var delay = scheduledUtc - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                BackgroundJob.Schedule<ITenderPublishingService>(
                    service => service.PublishScheduledTendersAsync(),
                    delay
                );

                return Json(new
                {
                    success = true,
                    tenderNumber = scheduledTender.TenderNumber,
                    redirectUrl = Url.Action("Index", "TenderAdmin"),
                    scheduledTendersUrl = Url.Action("ScheduledIndex", "TenderAdmin")
                });
            }

            // --- Immediate Tender Logic ---
            // Get authoritative South African time for publishing
            var saNow = _saTimeService.GetCurrentSouthAfricanTime();
            var datePublished = saNow.ToDateTimeUnspecified();

            var tender = new Tender
            {
                TenderType = model.TenderType,
                TenderNumber = model.TenderNumber,
                ClosingDate = model.ClosingDate.Value,
                ClosingTime = model.ClosingTime,
                Status = model.Status,
                Title = model.Title,
                Description = model.Description,
                DatePublished = datePublished,
                DraftId = model.DraftId,
                Documents = new List<TenderDocument>(),
                AwardedTender = null
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
                            BlobName = blobFileName,
                            FilePath = blobFileName,
                            TenderId = tender.Id
                        });
                    }
                }
            }

            _context.Tenders.Add(tender);
            await _context.SaveChangesAsync();

            // --- Draft Cleanup Logic ---
            if (model.DraftId.HasValue)
            {
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == model.DraftId.Value);

                if (draft != null)
                {
                    if (draft.Documents != null && draft.Documents.Any())
                        _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);

                    _context.TenderAdminsDraft.Remove(draft);
                    await _context.SaveChangesAsync();
                }
            }

            return Json(new
            {
                success = true,
                tenderNumber = tender.TenderNumber,
                redirectUrl = Url.Action("Index", "TenderAdmin")
            });
        }

        // Updated Index action to support tender status filtering
        public IActionResult Index(string status, int page = 1, int pageSize = 7)
        {
            var query = _context.Tenders.Include(t => t.Documents).AsQueryable();

            // Filter by status if provided (e.g. status = "open", "closed", "awarded", "cancelled")
            if (!string.IsNullOrEmpty(status))
            {
                var filter = status.Trim().ToLower();
                // Status values in DB are e.g. "Open Tender"
                query = query.Where(t => t.Status.ToLower().Contains(filter));
            }

            var totalItems = query.Count();
            var tenders = query
                .OrderByDescending(t => t.DatePublished)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // Blob storage for document SAS URIs (optional)
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
            ViewBag.Status = status; // For pagination links

            return View(tenders);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDraft()
        {
            // Access the posted form data and uploaded files from the HTTP request
            var form = Request.Form;
            var files = Request.Form.Files;

            // Attempt to retrieve DraftId from the form data (if it exists)
            Guid draftGuid;
            TenderDraft draft = null;
            if (Guid.TryParse(form["DraftId"], out draftGuid))
            {
                // If DraftId is present and valid, try to fetch the existing draft (including any attached documents) from the database
                draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == draftGuid);
            }

            if (draft == null)
            {
                // If no existing draft is found, create a new one
                draft = new TenderDraft
                {
                    DraftId = Guid.NewGuid(),                        // Generate a new unique DraftId
                    CreatedDate = _saTimeService.GetCurrentSouthAfricanTime()
                    .ToDateTimeUnspecified(),                       // Set creation date to now (UTC)
                    Documents = new List<TenderDraftDocument>()      // Initialize the documents collection
                };
                _context.TenderAdminsDraft.Add(draft);               // Add the new draft to EF context for saving
            }
            else
            {
                // If updating an existing draft, set the last modified date to now (UTC)
                draft.LastModifiedDate = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified();

                // Optionally: Uncomment below lines if you want to replace old documents with new ones
                // _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);
                // draft.Documents.Clear();
            }

            // Update draft fields from the form data (simple mapping; assumes all keys exist)
            draft.TenderType = form["TenderType"];
            draft.TenderNumber = form["TenderNumber"];
            draft.Status = form["Status"];
            draft.Title = form["Title"];
            draft.Description = form["Description"];

            // Safely parse and assign ClosingDate and ClosingTime if provided and valid
            if (DateTime.TryParse(form["ClosingDate"], out var closingDate))
                draft.ClosingDate = closingDate;
            if (TimeSpan.TryParse(form["ClosingTime"], out var closingTime))
                draft.ClosingTime = closingTime;

            // Handle file uploads and persist them in Azure Blob Storage
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            foreach (var file in files)
            {
                if (file.Length > 0)
                {
                    // Generate a unique blob file name using the draft's ID and a new GUID
                    var blobFileName = $"{draft.DraftId}/{Guid.NewGuid()}_{file.FileName}";
                    using var stream = file.OpenReadStream();
                    // Upload the file to Azure Blob Storage and get the blob URL
                    var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                    // Add a new document record to the draft's documents collection
                    draft.Documents.Add(new TenderDraftDocument
                    {
                        FileName = file.FileName,       // Original uploaded file name
                        FilePath = blobFileName         // Blob storage path for retrieval/deletion
                    });
                }
            }

            // Save all changes (new/updated draft and documents) to the database
            await _context.SaveChangesAsync();

            // Return a JSON response indicating success, with the draft's unique ID
            return Json(new
            {
                success = true,
                message = "Draft saved successfully.",
                draftId = draft.DraftId,
                tenderNumber = draft.TenderNumber,
                draftIndexUrl = Url.Action("DraftIndex", "TenderAdmin"),
                editDraftUrl = Url.Action("EditDraft", "TenderAdmin", new { id = draft.DraftId }),
                redirectUrl = Url.Action("Index", "TenderAdmin")
            });
        }


        [HttpGet]
        public IActionResult DraftIndex(int page = 1, int pageSize = 7)
        {
            var query = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .OrderByDescending(d => d.CreatedDate);

            var totalItems = query.Count();
            var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            var drafts = query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;

            return View(drafts);
        }


        [HttpGet]
        public IActionResult EditDraft(int id)
        {
            // Attempt to retrieve the draft tender with the specified ID from the database.
            // Include the related Documents navigation property so any attached files are available for editing.
            var draft = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .FirstOrDefault(d => d.Id == id);

            // If the draft is not found (invalid ID or deleted), return a 404 Not Found response.
            if (draft == null)
                return NotFound();

            // Map the retrieved draft entity to a TenderViewModel.
            // Only map the fields that exist in the draft (leave missing fields as null/default).
            // This view model will be used to populate the edit form in the view.
            var model = new TenderViewModel
            {
                DraftId = draft.DraftId,                      // Unique identifier for the draft
                TenderType = draft.TenderType,                // Type of tender (e.g., RFI, RFP)
                TenderNumber = draft.TenderNumber,            // Reference number for the draft tender
                ClosingDate = draft.ClosingDate, // nullable DateTime? property   // Date the tender closes; will set below if null
                ClosingTime = draft.ClosingTime,              // Time the tender closes on the closing date
                Status = draft.Status,                        // Current status of the draft (e.g., Draft, Pending)
                Title = draft.Title,                          // Title for the tender
                Description = draft.Description,              // Description/details for the tender
                                                              // Map each existing draft document to a view model for display in the UI.
                ExistingDocuments = draft.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,                             // Unique document ID
                    FileName = doc.FileName,                 // Original filename for display
                    FilePath = doc.FilePath                  // File path or blob storage path
                }).ToList() ?? new List<TenderDocumentViewModel>() // If no documents, provide empty list
            };

            // If the ClosingDate is not set (null), default to today's date.
            // This prevents validation errors in the view when rendering the edit form.
            //if (draft.ClosingDate == null)
            //    model.ClosingDate = DateTime.Today;

            // Reuse the "Create" view for editing drafts, passing in the populated model.
            // The view will display the draft's details for editing.
            return View("Create", model);
        }


        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var tender = await _context.Tenders
                .Include(t => t.AwardedTender)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

            // Load main tender documents (AwardedTenderId is null)
            var mainDocs = await _context.TenderDocuments
                .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId == null)
                .ToListAsync();

            var mainDocumentList = mainDocs.Select(doc => new TenderDocumentViewModel
            {
                Id = doc.Id,
                FileName = doc.FileName,
                FilePath = doc.FilePath
            }).ToList();

            // Load awarded documents if any
            List<TenderDocumentViewModel> awardedDocumentList = new();
            if (tender.AwardedTender != null)
            {
                var awardedDocs = await _context.TenderDocuments
                    .Where(doc => doc.AwardedTenderId == tender.AwardedTender.Id)
                    .ToListAsync();

                awardedDocumentList = awardedDocs.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    FilePath = doc.FilePath
                }).ToList();
            }

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
                AwardedTender = tender.AwardedTender?.AwardedCompanyName,
                ExistingDocuments = mainDocumentList,
                AwardedDocuments = awardedDocumentList
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
                .Include(t => t.AwardedTender)
                    .ThenInclude(at => at.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

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
                if (dto.Status == "Awarded Tender" && tender.AwardedTender != null)
                {
                    var docsToRemove = tender.AwardedTender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                    foreach (var doc in docsToRemove)
                    {
                        await blobService.DeleteFileAsync(doc.FilePath);
                        _context.TenderDocuments.Remove(doc);
                    }
                }
                else
                {
                    var docsToRemove = tender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                    foreach (var doc in docsToRemove)
                    {
                        await blobService.DeleteFileAsync(doc.FilePath);
                        _context.TenderDocuments.Remove(doc);
                    }
                }
            }

            // Handle new PDF uploads
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                // If Awarded Tender, ensure AwardedTender exists and has correct Id before adding docs
                if (dto.Status == "Awarded Tender")
                {
                    AwardedTender awardedTender = tender.AwardedTender;
                    if (awardedTender == null)
                    {
                        // Create and save AwardedTender first to get its Id
                        awardedTender = new AwardedTender
                        {
                            AwardedCompanyName = dto.AwardedTender,
                            TenderId = tender.Id
                        };
                        _context.AwardedTenders.Add(awardedTender);
                        await _context.SaveChangesAsync();
                        tender.AwardedTenderId = awardedTender.Id;
                        tender.AwardedTender = awardedTender; // Sync navigation property
                    }
                    else
                    {
                        awardedTender.AwardedCompanyName = dto.AwardedTender;
                    }

                    // Now upload awarded documents
                    foreach (var file in dto.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{file.FileName}";
                            using var stream = file.OpenReadStream();
                            var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                            var newDoc = new TenderDocument
                            {
                                FileName = file.FileName,
                                BlobName = blobFileName,
                                FilePath = blobFileName,
                                TenderId = tender.Id,
                                AwardedTenderId = awardedTender.Id // CORRECT: set after AwardedTender is saved!
                            };
                            _context.TenderDocuments.Add(newDoc);
                        }
                    }
                }
                else
                {
                    // Main tender documents
                    foreach (var file in dto.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{file.FileName}";
                            using var stream = file.OpenReadStream();
                            var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                            var newDoc = new TenderDocument
                            {
                                FileName = file.FileName,
                                BlobName = blobFileName,
                                FilePath = blobFileName,
                                TenderId = tender.Id,
                                AwardedTenderId = null
                            };
                            _context.TenderDocuments.Add(newDoc);
                        }
                    }
                }
            }

            // AwardedTender update logic
            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                if (tender.AwardedTender == null)
                {
                    // Already handled above in upload section, but just in case
                    var awardedTender = new AwardedTender
                    {
                        AwardedCompanyName = dto.AwardedTender,
                        TenderId = tender.Id
                    };
                    _context.AwardedTenders.Add(awardedTender);
                    await _context.SaveChangesAsync();
                    tender.AwardedTenderId = awardedTender.Id;
                }
                else
                {
                    tender.AwardedTender.AwardedCompanyName = dto.AwardedTender;
                }
            }
            else
            {
                tender.AwardedTenderId = null;
            }

            await _context.SaveChangesAsync();

            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                return Json(new
                {
                    success = true,
                    tenderNumber = tender.TenderNumber,
                    redirectUrl = Url.Action("Index", "TenderAdmin"),
                    awarded = true
                });
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult ScheduledIndex(int page = 1, int pageSize = 7)
        {
            // Get the total number of scheduled tenders in the database.
            // This is needed for pagination calculation and displaying the total count.
            var totalItems = _context.ScheduledTenders.Count();

            // Fetch the scheduled tenders for the current page.
            // - Includes related Documents for each tender for display in the view.
            // - Orders tenders by their scheduled publish date/time in descending order (most recent first).
            // - Skips tenders from previous pages to get items for the current page.
            // - Takes only the number of items equal to the page size (for paging).
            var scheduledTenders = _context.ScheduledTenders
                .Include(t => t.Documents)
                .OrderByDescending(t => t.ScheduledPublishDateTime)
                .Skip((page - 1) * pageSize) // Calculate how many items to skip based on current page
                .Take(pageSize)              // Take only the items for this page
                .ToList();

            // Store pagination and count info in ViewBag for use in the view (UI):
            ViewBag.CurrentPage = page;                                // The current page number
            ViewBag.PageSize = pageSize;                               // The number of items per page
            ViewBag.TotalItems = totalItems;                           // Total number of scheduled tenders (for showing total count)
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize); // Total number of pages (for navigation controls)

            // Return the view, passing in the list of scheduled tenders for the current page.
            return View(scheduledTenders);
        }


        [HttpGet]
        public async Task<IActionResult> EditScheduled(int id)
        {
            // Retrieve the scheduled tender from the database, including its associated documents,
            // based on the provided unique tender ID.
            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            // If no scheduled tender is found for the given ID, return a 404 Not Found response.
            if (scheduledTender == null)
                return NotFound();

            // Create a new DTO (Data Transfer Object) to transfer tender data to the view.
            // Populate the DTO with all relevant tender details, including:
            // - Tender metadata (type, number, title, description, status, etc.)
            // - Closing date and time
            // - Scheduled publish datetime (for display or editing)
            // - Existing documents transformed into view models for the front end
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
                ScheduledPublishDateTime = scheduledTender.ScheduledPublishDateTime, // Provide the scheduled publish datetime to the view

                // Map each existing document in the tender to a view model for the UI
                ExistingDocuments = scheduledTender.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    FilePath = doc.FilePath
                }).ToList() ?? new List<TenderDocumentViewModel>() // Ensure it's never null for the view
            };

            // Optionally: If you want to allow editing of the scheduled date and time separately,
            // you can extract and set ScheduledDate and ScheduledTime here for the DTO.

            // Render the "EditScheduled" view, passing in the populated DTO.
            // The view will display all tender details and existing documents for editing.
            return View("EditScheduled", dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditScheduled(int id, TenderEditDto dto)
        {
            // If the model state is invalid (e.g., required fields missing), redisplay the form with validation errors
            if (!ModelState.IsValid)
            {
                return View("EditScheduled", dto);
            }

            // Retrieve the scheduled tender from the database, including its associated documents, using the provided ID
            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            // If no such scheduled tender exists, return a 404 Not Found response
            if (scheduledTender == null)
                return NotFound();

            // Update the scheduled tender's core fields with values from the DTO (data transfer object)
            scheduledTender.TenderType = dto.TenderType;
            scheduledTender.TenderNumber = dto.TenderNumber;
            scheduledTender.ClosingDate = dto.ClosingDate.Value;
            scheduledTender.ClosingTime = dto.ClosingTime;
            scheduledTender.Status = dto.Status;
            scheduledTender.Title = dto.Title;
            scheduledTender.Description = dto.Description;

            // If the tender is scheduled and has a scheduled date and time,
            // calculate the scheduled publish datetime in UTC based on the user's local time zone
            if (dto.IsScheduled && dto.ScheduledDate.HasValue && dto.ScheduledTime.HasValue)
            {
                // Combine date and time as South African LocalDateTime
                var scheduledSaLocal = new NodaTime.LocalDateTime(
                    dto.ScheduledDate.Value.Year,
                    dto.ScheduledDate.Value.Month,
                    dto.ScheduledDate.Value.Day,
                    dto.ScheduledTime.Value.Hours,
                    dto.ScheduledTime.Value.Minutes,
                    dto.ScheduledTime.Value.Seconds
                );
                // Convert to UTC instant using your service
                var scheduledUtcInstant = _saTimeService.ConvertSaLocalToUtc(scheduledSaLocal);
                scheduledTender.ScheduledPublishDateTime = scheduledUtcInstant.ToDateTimeUtc();
            }

            // Initialize the Azure Blob Storage service for document management
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // Handle deletion of documents:
            // For each document ID marked for deletion, remove it from both blob storage and the database
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                // Find the documents in the scheduled tender that need to be deleted
                var docsToRemove = scheduledTender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    // Delete the file from Azure Blob Storage
                    await blobService.DeleteFileAsync(doc.FilePath);
                    // Remove the document record from the database
                    _context.ScheduledTendersDocuments.Remove(doc);
                }
            }

            // Handle upload of new documents:
            // For each new file uploaded, upload it to blob storage and add a record to the scheduled tender's documents
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        // Generate a unique blob file name for storage
                        var blobFileName = $"{scheduledTender.Id}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        // Upload the file to Azure Blob Storage
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        // Add the new document record to the scheduled tender
                        scheduledTender.Documents.Add(new ScheduledTenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName,
                            ScheduledTenderId = scheduledTender.Id
                        });
                    }
                }
            }

            // Persist all changes (field updates, document deletions, and additions) to the database
            await _context.SaveChangesAsync();

            // Redirect the user to the index page for scheduled tenders after successful edit
            return RedirectToAction("ScheduledIndex");
        }



        //---------------------------------------------------------------------------------------------------
        // Reports and Supplier Report Actions
        //---------------------------------------------------------------------------------------------------

        [HttpGet]
        public IActionResult Reports_Index()
        {
            // Retrieve all tender records from the database.
            // This uses Entity Framework to query the Tenders table and return the results as a list.
            // No filtering, sorting, or related data is included—this gets all tenders as-is.
            var tenders = _context.Tenders.ToList();

            // Pass the list of tenders to the view for display.
            // The view can then iterate through the tenders and present them in a table or other format.
            return View(tenders);
        }

        [HttpGet]
        public IActionResult tender_Report(int id)
        {
            // Retrieve the tender record by its ID from the database
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);

            // If no tender is found, return a 404 Not Found response
            if (tender == null)
                return NotFound();

            // Load all applications for this tender, including related OVRS_User info
            var applications = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => a.TenderId == id)
                .ToList();

            // Build a dictionary of supplier info from the legacy database
            // - AsNoTracking: disables EF tracking for performance when reading
            // - ToList: brings all records into memory so GroupBy works
            // - GroupBy: groups suppliers by user ID to avoid duplicates
            // - ToDictionary: creates a dictionary with user ID as key and first supplier record as value
            var supplierDict = _legacyContext.TblSuppliers
                .AsNoTracking()
                .ToList()
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            int localCount = 0;   // Track local suppliers
            int foreignCount = 0; // Track foreign suppliers

            // Count the number of local and foreign suppliers for this tender
            foreach (var app in applications)
            {
                var user = app.OVRS_User;
                // Only count if the application has an OVRS_User and a valid LegacyUserId
                if (user != null && user.LegacyUserId.HasValue && supplierDict.TryGetValue(user.LegacyUserId.Value, out var supplier))
                {
                    // Supplier type: 1 = Local, 2 = Foreign
                    if (supplier.LocalForeigner == 1)
                        localCount++;
                    else if (supplier.LocalForeigner == 2)
                        foreignCount++;
                }
            }

            // Create a view model for the tender report, including supplier counts
            var viewModel = new TenderReportViewModel
            {
                Tender = tender,
                LocalSupplierCount = localCount,
                ForeignSupplierCount = foreignCount
            };

            // Return the view, passing the view model for rendering
            return View(viewModel);
        }

        [HttpGet]
        public IActionResult GenerateTenderSupplierReport(int id, DateTime? startDate = null, DateTime? endDate = null)
        {
            // Retrieve the tender record by its ID from the database
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);
            if (tender == null)
                return NotFound();

            // Prepare a query for all applications for this tender, including OVRS_User info
            var query = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => a.TenderId == id);

            // If a date range is specified, filter applications by DateApplied
            if (startDate.HasValue && endDate.HasValue)
            {
                // Make endDate inclusive (end of day)
                var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);
                query = query.Where(a => a.DateApplied >= startDate && a.DateApplied <= endDateInclusive);
            }
            var applications = query.ToList();

            // Load supplier info from the legacy DB into a dictionary, avoiding duplicates
            var supplierDict = _legacyContext.TblSuppliers
                .AsNoTracking()
                .ToList()
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            // Build list of supplier PDF view models for each application
            var pdfInfos = applications.Select(app =>
            {
                var user = app.OVRS_User;
                string companyName = "-";
                string supplierType = "-";
                // Lookup supplier details using LegacyUserId
                if (user != null && user.LegacyUserId.HasValue && supplierDict.TryGetValue(user.LegacyUserId.Value, out var supplier))
                {
                    // Prefer TradingName, fallback to LegalName
                    companyName = supplier.TradingName ?? supplier.LegalName ?? "-";
                    // Map supplier type code to string
                    supplierType = (supplier.LocalForeigner == 1) ? "Local" :
                                   (supplier.LocalForeigner == 2) ? "Foreign" : "-";
                }
                return new OVRS_UserPdfInfoViewModel
                {
                    Id = user?.Id ?? 0,
                    LegacyUserId = user?.LegacyUserId ?? 0,
                    CompanyName = companyName,
                    SupplierType = supplierType
                };
            }).ToList();

            // Generate the supplier report PDF from the list
            var pdfBytes = _pdfService.GenerateSupplierReport(tender, pdfInfos);

            // Return the PDF file as a download, naming it with the tender number
            return File(pdfBytes, "application/pdf", $"SupplierReport_Tender_{tender.TenderNumber}.pdf");
        }

        [HttpGet]
        public IActionResult GenerateClosedTendersSummaryReport(DateTime? startDate, DateTime? endDate)
        {
            // Validate that both start and end dates are provided
            if (!startDate.HasValue || !endDate.HasValue)
                return BadRequest("Start and end date required");

            // Make endDate inclusive (end of day)
            var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);

            // Get all tenders that are closed and in the specified date range
            var tenders = _context.Tenders
                .Where(t => t.Status != null && t.Status.ToLower().Contains("closed")
                         && t.ClosingDate >= startDate && t.ClosingDate <= endDateInclusive)
                .ToList();

            // Get all tender IDs for the closed tenders
            var tenderIds = tenders.Select(t => t.Id).ToList();

            // Get all applications for those tenders, including OVRS_User info
            var allApps = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => tenderIds.Contains(a.TenderId))
                .ToList();

            // Build supplier dictionary from legacy DB (avoid duplicate keys)
            var supplierDict = _legacyContext.TblSuppliers
                .AsNoTracking()
                .ToList()
                .GroupBy(s => s.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            // For each tender, build a summary row counting local/foreign suppliers
            var summaryList = tenders.Select(tender =>
            {
                // Get all applications for this tender
                var apps = allApps.Where(a => a.TenderId == tender.Id).ToList();

                int local = 0;   // Local supplier count
                int foreign = 0; // Foreign supplier count
                                 // For each application, look up supplier info and increment counters
                foreach (var app in apps)
                {
                    var user = app.OVRS_User;
                    if (user != null && user.LegacyUserId.HasValue && supplierDict.TryGetValue(user.LegacyUserId.Value, out var supplier))
                    {
                        if (supplier.LocalForeigner == 1)
                            local++;
                        else if (supplier.LocalForeigner == 2)
                            foreign++;
                    }
                }
                int total = local + foreign;

                // Create the summary row for this tender
                return new ClosedTenderSummaryRow
                {
                    TenderType = tender.TenderType ?? "-",
                    ClosedTenderCount = 1,
                    LocalSuppliers = local,
                    ForeignSuppliers = foreign,
                    TotalApplicants = total
                };
            }).ToList();

            // Generate the summary PDF report for closed tenders
            var pdfBytes = ClosedTendersSummaryPdfService.GenerateSummaryReport(summaryList, startDate.Value, endDate.Value);

            // Return the PDF file as a download, naming it with the date range
            return File(pdfBytes, "application/pdf", $"ClosedTendersSummary_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.pdf");
        }
    }

}

