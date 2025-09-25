using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using SABC_Phase2.Data;
using SABC_Phase2.Models;
using SABC_Phase2.Models.Administrator;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;
using System.Globalization;
using System.Security.Claims;
using Azure.Identity;


namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for administering tender creation and listing.
    /// Supports both immediate and scheduled publishing via Hangfire and Azure Blob Storage for document uploads.
    /// </summary>
    public class TenderAdminController : Controller
    {
        private readonly AuditLogService _auditLogService;
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
        public TenderAdminController(Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration, IWebHostEnvironment env, TenderReportPdfService pdfService, SouthAfricanTimeService saTimeService, AuditLogService auditLogService)
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

            _auditLogService = auditLogService;
        }

        // Helper to get current admin info
        private async Task<(int adminId, string adminEmail, string adminFullName)> GetCurrentAdminAsync()
        {
            var idStr = User.FindFirst("UserId")?.Value ?? User.FindFirst("AdminId")?.Value;
            int adminId = 0;
            int.TryParse(idStr, out adminId);

            var email = User.FindFirst("AdminEmail")?.Value
                ?? User.FindFirst(ClaimTypes.Email)?.Value
                ?? User.Identity?.Name;

            // Get full name from DB using adminId
            string fullName = email; // fallback
            if (adminId > 0)
            {
                var admin = await _context.Administrators.FindAsync(adminId);
                if (admin != null && !string.IsNullOrWhiteSpace(admin.FirstName) && !string.IsNullOrWhiteSpace(admin.LastName))
                {
                    fullName = $"{admin.FirstName} {admin.LastName}";
                }
            }

            return (adminId, email, fullName);
        }

        [HttpGet]
        public IActionResult Create()
        {
            var currentSaTime = _saTimeService.GetCurrentSouthAfricanTime();
            ViewBag.CurrentSaDate = currentSaTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ViewBag.CurrentSaTime = currentSaTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture); // Add this
            ViewBag.CurrentSaDateTime = currentSaTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return View(new TenderViewModel());
        }

        [Authorize(Roles = "Tender_Administrator")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            if (!User.Identity.IsAuthenticated || !User.IsInRole("Tender_Administrator"))
                return Forbid();

            // --- Get Admin Info ONCE for audit logging ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();

            // --- TENDER NUMBER UNIQUENESS VALIDATION ---
            if (!string.IsNullOrWhiteSpace(model.TenderNumber))
            {
                var tenderNumber = model.TenderNumber.Trim();

                bool existsInRegular = await _context.Tenders.AnyAsync(t => t.TenderNumber == tenderNumber);
                bool existsInDraft = await _context.TenderAdminsDraft.AnyAsync(d => d.TenderNumber == tenderNumber);
                bool existsInScheduled = await _context.ScheduledTenders.AnyAsync(s => s.TenderNumber == tenderNumber);

                if (existsInRegular)
                    ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Published Tender\".");
                else if (existsInScheduled)
                    ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Scheduled Tender\".");
            }

            // If ModelState is invalid, return the View so inline errors display
            if (!ModelState.IsValid)
                return View(model);

            var sharePointService = new SharePointService(_configuration);

            // --- Scheduled Tender Logic ---
            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                try
                {
                    var scheduledLocal = model.ScheduledDate.Value.Date + model.ScheduledTime.Value;
                    var scheduledSaLocal = new NodaTime.LocalDateTime(
                        scheduledLocal.Year, scheduledLocal.Month, scheduledLocal.Day,
                        scheduledLocal.Hour, scheduledLocal.Minute, scheduledLocal.Second
                    );
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

                    var safeTenderFolder = SanitizeHelper.ToSharePointSafeFolderName(scheduledTender.TenderNumber);

                    if (model.UploadedFiles != null && model.UploadedFiles.Any())
                    {
                        foreach (var file in model.UploadedFiles)
                        {
                            if (file.Length > 0)
                            {
                                using var stream = file.OpenReadStream();
                                var sharePointUrl = await sharePointService.UploadDocumentAsync(
                                    safeTenderFolder,
                                    stream,
                                    file.FileName);

                                scheduledTender.Documents.Add(new ScheduledTenderDocument
                                {
                                    FileName = file.FileName,
                                    SharePointPath = sharePointUrl
                                });
                            }
                        }
                    }

                    _context.ScheduledTenders.Add(scheduledTender);
                    await _context.SaveChangesAsync();

                    // --- AUDIT LOG: Log scheduled tender creation ---
                    await _auditLogService.LogAsync(
                        adminId,
                        adminEmail,
                        adminFullName,
                        "ScheduleTender",
                        $"Scheduled Tender \"{scheduledTender.TenderNumber}\" was created by {adminFullName} ({adminEmail})"
                    );

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
                catch (Exception ex)
                {
                    Console.WriteLine($"Error scheduling tender: {ex}");
                    return Json(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            }

            // --- Immediate Tender Logic ---
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

            var safeTenderFolder2 = SanitizeHelper.ToSharePointSafeFolderName(tender.TenderNumber);

            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                foreach (var file in model.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();
                        var sharePointUrl = await sharePointService.UploadDocumentAsync(
                            safeTenderFolder2,
                            stream,
                            file.FileName);

                        tender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl,
                            TenderId = tender.Id
                        });
                    }
                }
            }

            if (model.DraftId.HasValue)
            {
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == model.DraftId.Value);

                if (draft != null && draft.Documents != null && draft.Documents.Any())
                {
                    foreach (var draftDoc in draft.Documents)
                    {
                        if (!tender.Documents.Any(d => d.FileName == draftDoc.FileName))
                        {
                            tender.Documents.Add(new TenderDocument
                            {
                                FileName = draftDoc.FileName,
                                SharePointPath = draftDoc.SharePointPath,
                                TenderId = tender.Id
                            });
                        }
                    }
                }
            }

            _context.Tenders.Add(tender);
            await _context.SaveChangesAsync();

            // --- AUDIT LOG: Log immediate tender creation ---
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                "CreateTender",
                $"Tender \"{tender.TenderNumber}\" was published by {adminFullName} ({adminEmail})"
            );

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

                    // --- AUDIT LOG: Log draft deletion after publish ---
                    await _auditLogService.LogAsync(
                        adminId,
                        adminEmail,
                        adminFullName,
                        "DeleteDraftAfterPublish",
                        $"Draft for tender \"{tender.TenderNumber}\" was published by {adminFullName} ({adminEmail})."
                    );
                }
            }

            return Json(new
            {
                success = true,
                tenderNumber = tender.TenderNumber,
                redirectUrl = Url.Action("Index", "TenderAdmin")
            });
        }



        public async Task<IActionResult> Index(string status = "", string type = "", string search = "", int page = 1, int pageSize = 7)

        {

            var query = _context.Tenders.Include(t => t.Documents).AsQueryable();

            // Map status to DB value

            string statusDbValue = MapStatus(status);

            if (!string.IsNullOrEmpty(statusDbValue))

                query = query.Where(t => t.Status.ToLower() == statusDbValue);

            // Type filter

            if (!string.IsNullOrEmpty(type))

            {

                string typeFilter = type.Trim().ToLower();

                query = query.Where(t => t.TenderType.ToLower() == typeFilter);

            }

            // Search filter

            if (!string.IsNullOrEmpty(search))

            {

                string searchLower = search.ToLower();

                query = query.Where(t =>

                    (t.TenderNumber != null && t.TenderNumber.ToLower().Contains(searchLower)) ||

                    (t.Title != null && t.Title.ToLower().Contains(searchLower)) ||

                    (t.Status != null && t.Status.ToLower().Contains(searchLower)) ||

                    (t.DatePublished != null && t.DatePublished.ToString().ToLower().Contains(searchLower))

                );

            }

            var totalItems = await query.CountAsync();

            var tenders = await query

                .OrderByDescending(t => t.DatePublished)

                .Skip((page - 1) * pageSize)

                .Take(pageSize)

                .ToListAsync();

            // --- Update: Use SharePointPath, no BlobService ---

            foreach (var tender in tenders)

            {

                foreach (var doc in tender.Documents)

                {

                    // Ensure FileName and SharePointPath are correct for view

                    // No BlobService, just keep the SharePointPath

                    // Example: doc.FileName and doc.SharePointPath are already set

                    // If you want to show a clickable link in your view, use doc.SharePointPath

                    // No need to modify doc here

                }

            }

            ViewBag.CurrentPage = page;

            ViewBag.PageSize = pageSize;

            ViewBag.TotalItems = totalItems;

            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            ViewBag.Status = status;

            ViewBag.Type = type;

            ViewBag.Search = search;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")

                return PartialView("Tender_Admin_TendersTablePartial_Index", tenders);

            return View(tenders);

        }


        private string MapStatus(string status)
        {
            switch (status?.ToLower())
            {
                case "open": return "open tender";
                case "closed": return "closed tender";
                case "awarded": return "awarded tender";
                case "cancelled": return "cancelled tender";
                default: return null;
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDraft()
        {
            var form = Request.Form;
            var files = Request.Form.Files;

            // Get draft ID if exists
            Guid draftGuid = Guid.Empty;
            if (Guid.TryParse(form["DraftId"], out var parsedDraftId))
            {
                draftGuid = parsedDraftId;
            }
            bool isNewDraft = draftGuid == Guid.Empty;

            // Uniqueness check: If another draft (not this one) uses this number
            var tenderNumber = form["TenderNumber"].ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(tenderNumber))
            {
                var existsInDraft = await _context.TenderAdminsDraft
                    .AnyAsync(d => d.TenderNumber == tenderNumber && (isNewDraft || d.DraftId != draftGuid));
                var existsInPublished = await _context.Tenders.AnyAsync(t => t.TenderNumber == tenderNumber);
                var existsInScheduled = await _context.ScheduledTenders.AnyAsync(s => s.TenderNumber == tenderNumber);

                if (existsInPublished || existsInDraft || existsInScheduled)
                {
                    // Create a model for validation display
                    var model = new TenderViewModel
                    {
                        DraftId = isNewDraft ? null : draftGuid,
                        TenderType = form["TenderType"],
                        TenderNumber = tenderNumber,
                        Title = form["Title"],
                        Description = form["Description"],
                        Status = form["Status"]
                    };

                    // Parse dates if they exist
                    if (DateTime.TryParse(form["ClosingDate"], out var parsedClosingDate))
                        model.ClosingDate = parsedClosingDate;

                    if (TimeSpan.TryParse(form["ClosingTime"], out var parsedClosingTime))
                        model.ClosingTime = parsedClosingTime;


                    // Load existing documents if editing
                    if (!isNewDraft)
                    {
                        var existingDraft = await _context.TenderAdminsDraft
                            .Include(d => d.Documents)
                            .FirstOrDefaultAsync(d => d.DraftId == draftGuid);

                        if (existingDraft?.Documents != null)
                        {
                            model.ExistingDocuments = existingDraft.Documents.Select(d => new TenderDocumentViewModel
                            {
                                Id = d.Id,
                                FileName = d.FileName,
                                SharePointPath = d.SharePointPath
                            }).ToList();
                        }
                    }

                    // Add specific error messages
                    if (existsInPublished)
                        ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Published Tender\".");
                    if (existsInDraft)
                        ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Draft Tender\".");
                    if (existsInScheduled)
                        ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Scheduled Tender\".");

                    // Return the view with validation errors
                    return View("Create", model);
                }
            }

            // Find or create draft
            TenderDraft draft = null;
            if (!isNewDraft)
            {
                draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == draftGuid);
            }

            // Detect old tender number for possible rename
            var oldTenderNumber = draft?.TenderNumber;

            if (draft == null)
            {
                draft = new TenderDraft
                {
                    DraftId = Guid.NewGuid(),
                    CreatedDate = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified(),
                    Documents = new List<TenderDraftDocument>()
                };
                _context.TenderAdminsDraft.Add(draft);
            }
            else
            {
                draft.LastModifiedDate = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified();
            }

            // Update draft fields
            draft.TenderType = form["TenderType"];
            draft.TenderNumber = tenderNumber;
            draft.Status = form["Status"];
            draft.Title = form["Title"];
            draft.Description = form["Description"];

            if (DateTime.TryParse(form["ClosingDate"], out var closingDate))
                draft.ClosingDate = closingDate;
            if (TimeSpan.TryParse(form["ClosingTime"], out var closingTime))
                draft.ClosingTime = closingTime;

            // Handle SharePoint folder rename if tender number changed
            var newTenderNumber = draft.TenderNumber;
            bool tenderNumberChanged = !string.IsNullOrWhiteSpace(oldTenderNumber)
                                       && !string.Equals(oldTenderNumber, newTenderNumber, StringComparison.OrdinalIgnoreCase)
                                       && draft.Documents != null && draft.Documents.Count > 0;
            if (tenderNumberChanged)
            {
                var sharePointService = new SharePointService(_configuration);
                try
                {
                    await sharePointService.RenameTenderFolderAsync(oldTenderNumber, newTenderNumber);

                    // Update SharePointPath for all draft docs if folder in URL
                    foreach (var doc in draft.Documents)
                    {
                        if (!string.IsNullOrEmpty(doc.SharePointPath) && doc.SharePointPath.Contains(oldTenderNumber))
                        {
                            doc.SharePointPath = doc.SharePointPath.Replace(oldTenderNumber, newTenderNumber);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Optionally log error
                }
            }

            // Handle Deletion of Draft Documents
            var docsToDelete = form["DocumentsToDelete"];
            if (draft.Documents != null && docsToDelete.Count > 0)
            {
                var idsToDelete = new HashSet<int>();
                foreach (var val in docsToDelete)
                {
                    foreach (var idStr in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(idStr, out var id))
                            idsToDelete.Add(id);
                    }
                }

                if (idsToDelete.Any())
                {
                    var sharePointService = new SharePointService(_configuration);
                    var docs = draft.Documents.Where(d => idsToDelete.Contains(d.Id)).ToList();
                    foreach (var doc in docs)
                    {
                        try
                        {
                            await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                        }
                        catch (Exception ex)
                        {
                            // Optionally log error
                        }
                        _context.TenderAdminsDraftDocuments.Remove(doc);
                    }
                    draft.Documents = draft.Documents.Where(d => !idsToDelete.Contains(d.Id)).ToList();
                }
            }

            // Handle Uploads
            if (files != null && files.Count > 0)
            {
                var sharePointService = new SharePointService(_configuration);
                foreach (var file in files)
                {
                    if (file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();

                        // Use sanitized folder name for SharePoint
                        string folderName = draft.TenderNumber;
                        if (!string.IsNullOrWhiteSpace(folderName))
                            folderName = SanitizeHelper.ToSharePointSafeFolderName(folderName);
                        else
                            folderName = draft.DraftId.ToString();

                        var sharePointUrl = await sharePointService.UploadDocumentAsync(
                            folderName,
                            stream,
                            file.FileName);

                        draft.Documents.Add(new TenderDraftDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();

            // --- AUDIT LOG: Log draft save ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
            string actionType = isNewDraft ? "CreateDraft" : "EditDraft";
            string description = isNewDraft
                ? $"Draft for Tender \"{draft.TenderNumber}\" was created by {adminFullName} ({adminEmail})"
                : $"Draft for Tender \"{draft.TenderNumber}\" was edited by {adminFullName} ({adminEmail})";
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                actionType,
                description
            );
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

        public async Task<IActionResult> DraftIndex(string search = "", string type = "", int page = 1, int pageSize = 7)

        {

            var query = _context.TenderAdminsDraft

                .Include(d => d.Documents)

                .OrderByDescending(d => d.CreatedDate)

                .AsQueryable();

            // Tender Type filter

            if (!string.IsNullOrEmpty(type))

            {

                string typeFilter = type.Trim().ToLower();

                query = query.Where(d => d.TenderType != null && d.TenderType.ToLower() == typeFilter);

            }

            // Search filter

            if (!string.IsNullOrEmpty(search))

            {

                string searchLower = search.ToLower();

                query = query.Where(d =>

                    (d.TenderNumber != null && d.TenderNumber.ToLower().Contains(searchLower)) || // Added

                    (d.Title != null && d.Title.ToLower().Contains(searchLower)) ||

                    (d.TenderType != null && d.TenderType.ToLower().Contains(searchLower)) ||

                    (d.CreatedDate.ToString().ToLower().Contains(searchLower))

                );

            }

            var totalItems = await query.CountAsync();

            var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            var drafts = await query

                .Skip((page - 1) * pageSize)

                .Take(pageSize)

                .ToListAsync();

            ViewBag.CurrentPage = page;

            ViewBag.PageSize = pageSize;

            ViewBag.TotalItems = totalItems;

            ViewBag.TotalPages = totalPages;

            ViewBag.Search = search;

            ViewBag.Type = type;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")

                return PartialView("Tender_Admin_DraftsTablePartial", drafts);

            return View(drafts);

        }

        [HttpGet]
        public IActionResult EditDraft(int id)
        {
            // Retrieve the draft tender with documents
            var draft = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .FirstOrDefault(d => d.Id == id);

            if (draft == null)
                return NotFound();

            // Map the draft entity to the view model, reflecting SharePointPath changes
            var model = new TenderViewModel
            {
                DraftId = draft.DraftId,
                TenderType = draft.TenderType,
                TenderNumber = draft.TenderNumber,
                ClosingDate = draft.ClosingDate,
                ClosingTime = draft.ClosingTime,
                Status = draft.Status,
                Title = draft.Title,
                Description = draft.Description,
                ExistingDocuments = draft.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    // Use SharePointPath instead of FilePath or BlobName
                    SharePointPath = doc.SharePointPath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

            // Optional: If ClosingDate is null, default to today's date
            // if (draft.ClosingDate == null)
            //     model.ClosingDate = DateTime.Today;

            return View("Create", model);
        }


        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            // Add this debug line at the start
            Console.WriteLine($"DEBUG: Editing tender with ID: {id}");

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
                SharePointPath = doc.SharePointPath
            }).ToList();

            // FIXED: Load awarded documents for this tender regardless of current AwardedTender status
            // This will find awarded documents that were previously uploaded for this tender
            List<TenderDocumentViewModel> awardedDocumentList = new();

            // First, try to get awarded docs from current AwardedTender if it exists
            if (tender.AwardedTender != null)
            {
                var currentAwardedDocs = await _context.TenderDocuments
                    .Where(doc => doc.AwardedTenderId == tender.AwardedTender.Id)
                    .ToListAsync();

                awardedDocumentList = currentAwardedDocs.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    SharePointPath = doc.SharePointPath
                }).ToList();
            }
            else
            {
                // IMPORTANT FIX: If no current AwardedTender, look for any awarded documents 
                // that were previously associated with this tender
                var previousAwardedDocs = await _context.TenderDocuments
                    .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null)
                    .ToListAsync();

                awardedDocumentList = previousAwardedDocs.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    SharePointPath = doc.SharePointPath
                }).ToList();
            }

            // Get companies that applied for this tender - FIXED VERSION
            var applicantCompanies = new List<ApplicantCompanyViewModel>();
            try
            {
                Console.WriteLine($"DEBUG: Looking for applicants for tender ID: {tender.Id}");

                var applicantUserIds = await _context.Applied_For_Tenders
                    .Where(aft => aft.TenderId == tender.Id)
                    .Select(aft => aft.OVRS_UserId)
                    .ToListAsync();

                Console.WriteLine($"DEBUG: Found {applicantUserIds.Count} applicant user IDs: {string.Join(", ", applicantUserIds)}");

                if (applicantUserIds.Any())
                {
                    // Get legacy IDs one by one to avoid OPENJSON issues
                    var userLegacyIds = new List<int>();
                    foreach (var userId in applicantUserIds)
                    {
                        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                        if (user != null && user.LegacyUserId.HasValue)
                        {
                            userLegacyIds.Add(user.LegacyUserId.Value);
                        }
                    }

                    Console.WriteLine($"DEBUG: Found {userLegacyIds.Count} legacy IDs: {string.Join(", ", userLegacyIds)}");

                    // Get company names one by one to avoid OPENJSON issues
                    foreach (var legacyId in userLegacyIds)
                    {
                        var supplier = await _legacyContext.TblSuppliers
                            .FirstOrDefaultAsync(s => s.UserId == legacyId);

                        if (supplier != null)
                        {
                            var companyName = !string.IsNullOrWhiteSpace(supplier.TradingName)
                                ? supplier.TradingName
                                : supplier.LegalName;

                            if (!string.IsNullOrWhiteSpace(companyName))
                            {
                                applicantCompanies.Add(new ApplicantCompanyViewModel
                                {
                                    UserId = supplier.UserId,
                                    CompanyName = companyName
                                });
                                Console.WriteLine($"DEBUG: Added company - ID: {supplier.UserId}, Name: {companyName}");
                            }
                        }
                    }

                    // Sort the companies
                    applicantCompanies = applicantCompanies.OrderBy(c => c.CompanyName).ToList();

                    Console.WriteLine($"DEBUG: Final applicant companies count: {applicantCompanies.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Exception in Edit GET: {ex.Message}");
                applicantCompanies = new List<ApplicantCompanyViewModel>();
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
                AwardedDocuments = awardedDocumentList, // This will now include previously uploaded awarded docs
                ApplicantCompanies = applicantCompanies
            };

            Console.WriteLine($"DEBUG: DTO ApplicantCompanies count: {dto.ApplicantCompanies.Count}");
            Console.WriteLine($"DEBUG: DTO AwardedDocuments count: {dto.AwardedDocuments.Count}");

            return View("Edit", dto);
        }

        [HttpGet]
        public async Task<IActionResult> GetApplicantCompanies(int tenderId)
        {
            try
            {
                // Debug: Log the tender ID
                Console.WriteLine($"DEBUG: Getting applicant companies for tender ID: {tenderId}");

                // Get all users who applied for this tender
                var applicantUserIds = await _context.Applied_For_Tenders
                    .Where(aft => aft.TenderId == tenderId)
                    .Select(aft => aft.OVRS_UserId)
                    .ToListAsync();

                Console.WriteLine($"DEBUG: Found {applicantUserIds.Count} applicant user IDs: {string.Join(", ", applicantUserIds)}");

                if (!applicantUserIds.Any())
                {
                    Console.WriteLine("DEBUG: No applicants found for this tender");
                    return Json(new { success = true, companies = new List<object>() });
                }

                // Get companies one by one to avoid OPENJSON issues
                var companies = new List<object>();

                foreach (var userId in applicantUserIds)
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                    if (user != null && user.LegacyUserId.HasValue)
                    {
                        var supplier = await _legacyContext.TblSuppliers
                            .FirstOrDefaultAsync(s => s.UserId == user.LegacyUserId.Value);

                        if (supplier != null)
                        {
                            var companyName = !string.IsNullOrWhiteSpace(supplier.TradingName)
                                ? supplier.TradingName
                                : supplier.LegalName;

                            if (!string.IsNullOrWhiteSpace(companyName))
                            {
                                // Include account status information
                                var companyObj = new
                                {
                                    UserId = supplier.UserId,
                                    CompanyName = companyName,
                                    IsAccountDeleted = user.AccountStatus == 0 // Check if account is deleted
                                };

                                companies.Add(companyObj);
                                Console.WriteLine($"DEBUG: Added company - ID: {supplier.UserId}, Name: {companyName}, Account Deleted: {user.AccountStatus == 0}");
                                Console.WriteLine($"DEBUG: Company object: {System.Text.Json.JsonSerializer.Serialize(companyObj)}");
                            }
                        }
                    }
                }

                // Sort the companies - active companies first, then deleted accounts
                companies = companies
                    .Cast<dynamic>()
                    .OrderBy(c => c.IsAccountDeleted) // Active accounts first
                    .ThenBy(c => c.CompanyName)
                    .Cast<object>()
                    .ToList();

                Console.WriteLine($"DEBUG: Found {companies.Count} companies with names");
                Console.WriteLine($"DEBUG: Final companies JSON: {System.Text.Json.JsonSerializer.Serialize(companies)}");

                var result = new { success = true, companies = companies };
                Console.WriteLine($"DEBUG: Final result JSON: {System.Text.Json.JsonSerializer.Serialize(result)}");

                return Json(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Exception occurred: {ex.Message}");
                Console.WriteLine($"DEBUG: Stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = "Failed to load applicant companies", error = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, TenderEditDto dto)
        {
            // --- TENDER NUMBER UNIQUENESS VALIDATION ON EDIT ---
            if (!string.IsNullOrWhiteSpace(dto.TenderNumber))
            {
                var tenderNumber = dto.TenderNumber.Trim();

                // Check other published tenders except this one
                bool existsInOtherPublished = await _context.Tenders
                    .AnyAsync(t => t.TenderNumber == tenderNumber && t.Id != id);

                // Check drafts
                bool existsInDraft = await _context.TenderAdminsDraft
                    .AnyAsync(d => d.TenderNumber == tenderNumber);

                // Check scheduled
                bool existsInScheduled = await _context.ScheduledTenders
                    .AnyAsync(s => s.TenderNumber == tenderNumber);

                if (existsInOtherPublished)
                    ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Published Tender\".");
                if (existsInDraft)
                    ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Draft Tender\".");
                if (existsInScheduled)
                    ModelState.AddModelError("TenderNumber", "This Tender Number was used for a \"Scheduled Tender\".");
            }

            if (!ModelState.IsValid)
            {
                // Check if this is an AJAX request
                if (Request.Headers["Content-Type"].ToString().Contains("multipart/form-data") ||
                    Request.Headers["RequestVerificationToken"].Any())
                {
                    return Json(new { success = false, message = "Invalid data submitted" });
                }
                return View("Edit", dto);
            }

            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .Include(t => t.AwardedTender)
                    .ThenInclude(at => at.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

            // --- EARLY CHECK: Prevent awarding to deleted company BEFORE ANY DATA CHANGES ---
            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                // 1. Find supplier by tradingname (or legalname if needed)
                var supplier = await _legacyContext.TblSuppliers
                    .FirstOrDefaultAsync(s => s.TradingName == dto.AwardedTender || s.LegalName == dto.AwardedTender);

                if (supplier != null)
                {
                    // 2. Find user in Phase 2 db by legacy user id
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.LegacyUserId == supplier.UserId);

                    if (user != null && user.AccountStatus == 0)
                    {
                        // 3. User account is deleted, block the award, return error for JS
                        return Json(new
                        {
                            success = false,
                            deletedAccount = true,
                            companyName = supplier.TradingName ?? supplier.LegalName
                        });
                    }
                }
            }

            // --- FIXED: AWARDED DOCUMENT VALIDATION ---
            if (dto.Status == "Awarded Tender")
            {
                // IMPORTANT FIX: Use the correct property for awarded document deletions
                var awardedDocsToDelete = dto.AwardedDocumentsToDelete ?? new List<int>();

                // Count awarded docs not marked for deletion from current AwardedTender
                int remainingAwardedDocsFromCurrent = tender.AwardedTender?.Documents
                    .Where(d => !awardedDocsToDelete.Contains(d.Id))
                    .Count() ?? 0;

                // ADDITIONAL FIX: Also check for previously uploaded awarded documents for this tender
                // (in case AwardedTender was set to null when status changed away from "Awarded Tender")
                int remainingPreviousAwardedDocs = 0;
                if (tender.AwardedTender == null)
                {
                    var previousAwardedDocs = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && !awardedDocsToDelete.Contains(doc.Id))
                        .CountAsync();
                    remainingPreviousAwardedDocs = previousAwardedDocs;
                }

                int totalExistingAwardedDocs = remainingAwardedDocsFromCurrent + remainingPreviousAwardedDocs;

                // Count new uploads
                int newAwardedUploads = dto.UploadedFiles?.Count ?? 0;

                if ((totalExistingAwardedDocs + newAwardedUploads) == 0)
                {
                    ModelState.AddModelError("", "You must upload at least one awarded tender document.");

                    // Re-populate AwardedDocuments for the view including previously uploaded docs
                    var allAwardedDocs = new List<TenderDocumentViewModel>();

                    if (tender.AwardedTender?.Documents != null)
                    {
                        allAwardedDocs.AddRange(tender.AwardedTender.Documents
                            .Where(d => !awardedDocsToDelete.Contains(d.Id))
                            .Select(d => new TenderDocumentViewModel
                            {
                                Id = d.Id,
                                FileName = d.FileName,
                                SharePointPath = d.SharePointPath
                            }));
                    }
                    else
                    {
                        // Add previously uploaded awarded docs
                        var previousDocs = await _context.TenderDocuments
                            .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && !awardedDocsToDelete.Contains(doc.Id))
                            .ToListAsync();

                        allAwardedDocs.AddRange(previousDocs.Select(d => new TenderDocumentViewModel
                        {
                            Id = d.Id,
                            FileName = d.FileName,
                            SharePointPath = d.SharePointPath
                        }));
                    }

                    dto.AwardedDocuments = allAwardedDocs;
                    return View("Edit", dto);
                }
            }

            var sharePointService = new SharePointService(_configuration);

            // FIXED: Handle awarded document deletions properly
            if (dto.AwardedDocumentsToDelete != null && dto.AwardedDocumentsToDelete.Any())
            {
                // Get documents to delete - check both current AwardedTender and previous awarded docs
                var docsToRemove = new List<TenderDocument>();

                if (tender.AwardedTender != null)
                {
                    docsToRemove.AddRange(tender.AwardedTender.Documents.Where(d => dto.AwardedDocumentsToDelete.Contains(d.Id)));
                }

                // Also check for previously uploaded awarded documents
                var previousAwardedDocs = await _context.TenderDocuments
                    .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && dto.AwardedDocumentsToDelete.Contains(doc.Id))
                    .ToListAsync();
                docsToRemove.AddRange(previousAwardedDocs);

                foreach (var doc in docsToRemove)
                {
                    await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    _context.TenderDocuments.Remove(doc);
                }
            }

            // Handle supporting document deletions
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var docsToRemove = tender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    _context.TenderDocuments.Remove(doc);
                }
            }

            // --- Detect tender number change ---
            var oldTenderNumber = tender.TenderNumber;
            var newTenderNumber = dto.TenderNumber;
            bool tenderNumberChanged = !string.Equals(oldTenderNumber, newTenderNumber, StringComparison.OrdinalIgnoreCase);

            tender.TenderType = dto.TenderType;
            tender.TenderNumber = newTenderNumber;
            tender.ClosingDate = dto.ClosingDate.Value;
            tender.ClosingTime = dto.ClosingTime;
            tender.Status = dto.Status;
            tender.Title = dto.Title;
            tender.Description = dto.Description;

            // --- RENAME SHAREPOINT FOLDER IF TENDER NUMBER CHANGED ---
            if (tenderNumberChanged)
            {
                try
                {
                    await sharePointService.RenameTenderFolderAsync(oldTenderNumber, newTenderNumber);

                    // Optional: update SharePointPath for all docs if folder in URL
                    foreach (var doc in tender.Documents)
                    {
                        if (!string.IsNullOrEmpty(doc.SharePointPath) && doc.SharePointPath.Contains(oldTenderNumber))
                        {
                            doc.SharePointPath = doc.SharePointPath.Replace(oldTenderNumber, newTenderNumber);
                        }
                    }
                    if (tender.AwardedTender?.Documents != null)
                    {
                        foreach (var doc in tender.AwardedTender.Documents)
                        {
                            if (!string.IsNullOrEmpty(doc.SharePointPath) && doc.SharePointPath.Contains(oldTenderNumber))
                            {
                                doc.SharePointPath = doc.SharePointPath.Replace(oldTenderNumber, newTenderNumber);
                            }
                        }
                    }

                    // ADDITIONAL FIX: Also update paths for previously uploaded awarded documents
                    var previousAwardedDocs = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null)
                        .ToListAsync();

                    foreach (var doc in previousAwardedDocs)
                    {
                        if (!string.IsNullOrEmpty(doc.SharePointPath) && doc.SharePointPath.Contains(oldTenderNumber))
                        {
                            doc.SharePointPath = doc.SharePointPath.Replace(oldTenderNumber, newTenderNumber);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log or show error as needed
                    ModelState.AddModelError("", $"Failed to rename SharePoint folder: {ex.Message}");
                    return View("Edit", dto);
                }
            }

            // --- Document uploads ---
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                if (dto.Status == "Awarded Tender")
                {
                    AwardedTender awardedTender = tender.AwardedTender;
                    if (awardedTender == null)
                    {
                        awardedTender = new AwardedTender
                        {
                            AwardedCompanyName = dto.AwardedTender,
                            TenderId = tender.Id
                        };
                        _context.AwardedTenders.Add(awardedTender);
                        await _context.SaveChangesAsync();
                        tender.AwardedTenderId = awardedTender.Id;
                        tender.AwardedTender = awardedTender;
                    }
                    else
                    {
                        awardedTender.AwardedCompanyName = dto.AwardedTender;
                    }

                    foreach (var file in dto.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            using var stream = file.OpenReadStream();
                            var sharePointUrl = await sharePointService.UploadDocumentAsync(
                                tender.TenderNumber,
                                stream,
                                file.FileName,
                                isAwarded: true
                            );

                            var newDoc = new TenderDocument
                            {
                                FileName = file.FileName,
                                SharePointPath = sharePointUrl,
                                TenderId = tender.Id,
                                AwardedTenderId = awardedTender.Id
                            };
                            _context.TenderDocuments.Add(newDoc);
                        }
                    }
                }
                else
                {
                    foreach (var file in dto.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            using var stream = file.OpenReadStream();
                            var sharePointUrl = await sharePointService.UploadDocumentAsync(
                                tender.TenderNumber,
                                stream,
                                file.FileName);

                            var newDoc = new TenderDocument
                            {
                                FileName = file.FileName,
                                SharePointPath = sharePointUrl,
                                TenderId = tender.Id,
                                AwardedTenderId = null
                            };
                            _context.TenderDocuments.Add(newDoc);
                        }
                    }
                }
            }

            // --- FIXED: AwardedTender logic ---
            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                if (tender.AwardedTender == null)
                {
                    // IMPORTANT FIX: When creating new AwardedTender, reassociate any previously uploaded awarded documents
                    var awardedTender = new AwardedTender
                    {
                        AwardedCompanyName = dto.AwardedTender,
                        TenderId = tender.Id
                    };
                    _context.AwardedTenders.Add(awardedTender);
                    await _context.SaveChangesAsync();
                    tender.AwardedTenderId = awardedTender.Id;

                    // Reassociate previously uploaded awarded documents to this new AwardedTender
                    var previousAwardedDocs = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && doc.AwardedTenderId != awardedTender.Id)
                        .ToListAsync();

                    foreach (var doc in previousAwardedDocs)
                    {
                        doc.AwardedTenderId = awardedTender.Id;
                    }
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

            // --- AUDIT LOG: Log tender edit or award ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
            bool isAwarded = dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender);

            if (isAwarded)
            {
                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    "EditTender",
                    $"Tender \"{tender.TenderNumber}\" was edited by {adminFullName} ({adminEmail}) and has awarded this Tender to {dto.AwardedTender}"
                );
            }
            else
            {
                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    "EditTender",
                    $"Tender \"{tender.TenderNumber}\" was edited by {adminFullName} ({adminEmail})"
                );
            }


            // Return JSON for regular success, set awarded appropriately
            return Json(new
            {
                success = true,
                tenderNumber = tender.TenderNumber,
                redirectUrl = Url.Action("Index", "TenderAdmin"),
                awarded = isAwarded
            });
        }

        [HttpGet]

        public async Task<IActionResult> ScheduledIndex(string search = "", string type = "", int page = 1, int pageSize = 7)

        {

            var query = _context.ScheduledTenders.Include(t => t.Documents).AsQueryable();

            // Filter by tender type

            if (!string.IsNullOrEmpty(type))

            {

                string typeFilter = type.Trim().ToLower();

                query = query.Where(t => t.TenderType.ToLower() == typeFilter);

            }

            // Search by keyword

            if (!string.IsNullOrEmpty(search))

            {

                string searchLower = search.ToLower();

                query = query.Where(t =>

                    (t.TenderNumber != null && t.TenderNumber.ToLower().Contains(searchLower)) || // <-- Added

                    (t.Title != null && t.Title.ToLower().Contains(searchLower)) ||

                    (t.Status != null && t.Status.ToLower().Contains(searchLower)) ||

                    (t.TenderType != null && t.TenderType.ToLower().Contains(searchLower)) ||

                    (t.ScheduledPublishDateTime != null && t.ScheduledPublishDateTime.ToString().ToLower().Contains(searchLower))

                );

            }

            var totalItems = await query.CountAsync();

            var scheduledTenders = await query

                .OrderByDescending(t => t.ScheduledPublishDateTime)

                .Skip((page - 1) * pageSize)

                .Take(pageSize)

                .ToListAsync();

            ViewBag.CurrentPage = page;

            ViewBag.PageSize = pageSize;

            ViewBag.TotalItems = totalItems;

            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            ViewBag.Search = search;

            ViewBag.Type = type;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")

                return PartialView("Tender_Admin_ScheduledTendersTablePartial", scheduledTenders);

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
                ScheduledPublishDateTime = scheduledTender.ScheduledPublishDateTime,
                ExistingDocuments = scheduledTender.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,
                    FileName = doc.FileName,
                    SharePointPath = doc.SharePointPath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

            // ✅ Get South African "today"
            var saNow = _saTimeService.GetCurrentSouthAfricanTime();
            ViewBag.MinDate = saNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // ✅ Closing date as max
            if (scheduledTender.ClosingDate != default)
            {
                ViewBag.MaxDate = scheduledTender.ClosingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return View("EditScheduled", dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditScheduled(int id, TenderEditDto dto)
        {
            if (!ModelState.IsValid)
            {
                return Json(new { success = false, message = "Invalid data submitted" });
            }

            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (scheduledTender == null)
                return Json(new { success = false, message = "Tender not found" });

            // --- UPDATE ENTITY ---
            scheduledTender.TenderType = dto.TenderType;
            scheduledTender.TenderNumber = dto.TenderNumber;
            scheduledTender.ClosingDate = dto.ClosingDate.Value;
            scheduledTender.ClosingTime = dto.ClosingTime;
            scheduledTender.Status = dto.Status;
            scheduledTender.Title = dto.Title;
            scheduledTender.Description = dto.Description;

            if (dto.IsScheduled && dto.ScheduledDate.HasValue && dto.ScheduledTime.HasValue)
            {
                var scheduledSaLocal = new NodaTime.LocalDateTime(
                    dto.ScheduledDate.Value.Year,
                    dto.ScheduledDate.Value.Month,
                    dto.ScheduledDate.Value.Day,
                    dto.ScheduledTime.Value.Hours,
                    dto.ScheduledTime.Value.Minutes,
                    dto.ScheduledTime.Value.Seconds
                );
                var scheduledUtcInstant = _saTimeService.ConvertSaLocalToUtc(scheduledSaLocal);
                scheduledTender.ScheduledPublishDateTime = scheduledUtcInstant.ToDateTimeUtc();
            }

            // --- DOCUMENT DELETION LOGIC ---
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var documentsToDelete = scheduledTender.Documents
                    .Where(d => dto.DocumentsToDelete.Contains(d.Id))
                    .ToList();

                var sharePointService = new SharePointService(_configuration);

                foreach (var doc in documentsToDelete)
                {
                    try
                    {
                        // Delete from SharePoint first
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting document from SharePoint: {ex.Message}");
                        // Continue with database deletion even if SharePoint deletion fails
                    }

                    // Remove from database
                    scheduledTender.Documents.Remove(doc);
                    _context.ScheduledTendersDocuments.Remove(doc);
                }
            }

            // --- NEW DOCUMENT UPLOAD LOGIC ---
            var sharePointServiceForUpload = new SharePointService(_configuration);
            var safeTenderFolder = SanitizeHelper.ToSharePointSafeFolderName(scheduledTender.TenderNumber);

            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();
                        var sharePointUrl = await sharePointServiceForUpload.UploadDocumentAsync(
                            safeTenderFolder,
                            stream,
                            file.FileName);

                        scheduledTender.Documents.Add(new ScheduledTenderDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl,
                            ScheduledTenderId = scheduledTender.Id
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();

            // --- AUDIT LOG: Log scheduled tender edit ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                "EditScheduledTender",
                $"Scheduled Tender \"{scheduledTender.TenderNumber}\" was edited by {adminFullName} ({adminEmail})"
            );

            return Json(new
            {
                success = true,
                tenderNumber = scheduledTender.TenderNumber,
                redirectUrl = Url.Action("ScheduledIndex", "TenderAdmin")
            });
        }







        //---------------------------------------------------------------------------------------------------
        // Reports and Supplier Report Actions
        //---------------------------------------------------------------------------------------------------

        [HttpGet]
        public IActionResult Reports_Index(string search = "", string type = "", int page = 1, int pageSize = 5)
        {
            var query = _context.Tenders.AsQueryable();

            // Only closed tenders
            query = query.Where(t => t.Status != null && t.Status.ToLower().Contains("closed"));

            // Tender Type filter
            if (!string.IsNullOrEmpty(type))
            {
                string typeFilter = type.Trim().ToLower();
                query = query.Where(t => t.TenderType.ToLower() == typeFilter);
            }

            // Search filter
            if (!string.IsNullOrEmpty(search))
            {
                string searchLower = search.ToLower();
                query = query.Where(t =>
                    (t.TenderNumber != null && t.TenderNumber.ToLower().Contains(searchLower)) ||
                    (t.Title != null && t.Title.ToLower().Contains(searchLower)) ||
                    (t.TenderType != null && t.TenderType.ToLower().Contains(searchLower)) ||
                    (t.ClosingDate != null && t.ClosingDate.ToString().ToLower().Contains(searchLower))
                );
            }

            var closedTenders = query.OrderByDescending(t => t.ClosingDate).ToList();
            int totalItems = closedTenders.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;
            ViewBag.Search = search;
            ViewBag.Type = type;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return PartialView("Tender_Admin_ReportsTablePartial", closedTenders);

            return View(closedTenders);
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
        public async Task<IActionResult> GenerateTenderSupplierReport(int id, DateTime? startDate = null, DateTime? endDate = null)
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

            // --- AUDIT LOG: Log supplier report generation ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                "GenerateTenderSupplierReport",
                $"Supplier report for Tender \"{tender.TenderNumber}\" was generated by {adminFullName} ({adminEmail})"
            );

            // Return the PDF file as a download, naming it with the tender number
            return File(pdfBytes, "application/pdf", $"SupplierReport_Tender_{tender.TenderNumber}.pdf");
        }
        [HttpGet]
        public async Task<IActionResult> GenerateClosedTendersSummaryReport(DateTime? startDate, DateTime? endDate)
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

            // --- AUDIT LOG: Log closed tenders summary report generation ---
            var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                "GenerateClosedTendersSummaryReport",
                $"Closed Tenders Summary report ({startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}) was generated by {adminFullName} ({adminEmail})"
            );

            // Return the PDF file as a download, naming it with the date range
            return File(pdfBytes, "application/pdf", $"ClosedTendersSummary_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.pdf");
        }
        // This deals with deleting entire draft


        [HttpPost]
        public async Task<IActionResult> DeleteDraft([FromBody] DeleteDraftReq request)
        {
            try
            {
                if (request == null || request.Id <= 0)
                {
                    return BadRequest(new { success = false, message = "Invalid draft ID" });
                }

                // Get the current user ID (optional, if you want to ensure users can only delete their own drafts)
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                // Find the draft with its documents
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.Id == request.Id);

                if (draft == null)
                {
                    return NotFound(new { success = false, message = "Draft not found" });
                }

                var sharePointService = new SharePointService(_configuration);

                // Delete the entire tender folder (including Admin docs and any other subfolders)
                if (draft.Documents != null && draft.Documents.Any())
                {
                    try
                    {
                        string tenderNumber = draft.TenderNumber;

                        // Use DeleteTenderFolderAsync instead of DeleteAdminDocsFolder
                        await sharePointService.DeleteTenderFolderAsync(tenderNumber);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting tender folder from SharePoint: {ex.Message}");
                    }

                    // Remove documents from database
                    _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);
                }

                // Remove the draft itself
                _context.TenderAdminsDraft.Remove(draft);
                await _context.SaveChangesAsync();

                // --- AUDIT LOG: Log draft deletion ---
                var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    "DeleteDraft",
                    $"Draft for Tender \"{draft.TenderNumber}\" (DraftId: {draft.Id}) was deleted by {adminFullName} ({adminEmail})"
                );

                return Ok(new { success = true, message = "Draft deleted successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting draft: {ex}");
                return StatusCode(500, new { success = false, message = "An error occurred while deleting the draft" });
            }
        }

        // This method deal with deleting a draft via edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDraftDocument([FromBody] DeleteDraftDocumentRequest req)
        {
            if (req == null || req.DocumentId <= 0)
                return Json(new { success = false, message = "Invalid request." });

            var doc = await _context.TenderAdminsDraftDocuments
                .FirstOrDefaultAsync(d => d.Id == req.DocumentId);

            if (doc == null)
                return Json(new { success = false, message = "Document not found." });

            var sharePointService = new SharePointService(_configuration);

            try
            {
                if (!string.IsNullOrWhiteSpace(doc.SharePointPath))
                    await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
            }
            catch (Exception ex)
            {
                // You might want to log this
                return Json(new { success = false, message = "Error deleting from SharePoint: " + ex.Message });
            }

            _context.TenderAdminsDraftDocuments.Remove(doc);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }


        [HttpPost]
        public async Task<IActionResult> DeleteScheduled([FromBody] DeleteDraftReq request)
        {
            try
            {
                if (request == null || request.Id <= 0)
                    return BadRequest(new { success = false, message = "Invalid scheduled tender ID" });

                var scheduledTender = await _context.ScheduledTenders
                    .Include(t => t.Documents)
                    .FirstOrDefaultAsync(t => t.Id == request.Id);

                if (scheduledTender == null)
                    return NotFound(new { success = false, message = "Scheduled tender not found" });

                var sharePointService = new SharePointService(_configuration);

                try
                {
                    // Delete the entire SharePoint folder for this scheduled tender
                    await sharePointService.DeleteTenderFolderAsync(scheduledTender.TenderNumber);
                }
                catch (Exception ex)
                {
                    // Log error but continue with DB cleanup
                    Console.WriteLine($"Error deleting scheduled tender folder from SharePoint: {ex.Message}");
                }

                // Remove documents from database if any
                if (scheduledTender.Documents != null && scheduledTender.Documents.Any())
                {
                    _context.ScheduledTendersDocuments.RemoveRange(scheduledTender.Documents);
                }

                // Remove the scheduled tender itself
                _context.ScheduledTenders.Remove(scheduledTender);
                await _context.SaveChangesAsync();

                // --- AUDIT LOG: Log scheduled tender deletion ---
                var (adminId, adminEmail, adminFullName) = await GetCurrentAdminAsync();
                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    "DeleteScheduledTender",
                    $"Scheduled Tender \"{scheduledTender.TenderNumber}\" was deleted by {adminFullName} ({adminEmail})"
                );

                return Ok(new { success = true, message = "Scheduled tender deleted successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting scheduled tender: {ex}");
                return StatusCode(500, new { success = false, message = "An error occurred while deleting the scheduled tender" });
            }
        }

        // ----------------------------------------------------------------------------------------------------------------------------------------------
        [HttpGet]
        public async Task<IActionResult> Users_Management(string search = "", string roleFilter = "all", string statusFilter = "all")
        {
            List<AdminUserRowViewModel> users;
            bool isOvrsUser = roleFilter == "OVRS_User";

            if (isOvrsUser)
            {
                // PHASE 2: Get *ALL* OVRS users (active or inactive)
                var ovrsPhase2Users = await _context.Users
                    .Where(u => u.Role == "OVRS_User" && u.LegacyUserId != null)
                    .ToListAsync();

                var legacyIds = ovrsPhase2Users.Select(u => u.LegacyUserId.Value).ToList();

                // PHASE 1: Get legacy users and suppliers
                var legacyUsers = await _legacyContext.TblUsers.ToListAsync();
                var suppliers = await _legacyContext.TblSuppliers.ToListAsync();

                var result = from phase2 in ovrsPhase2Users
                             join legacy in legacyUsers on phase2.LegacyUserId equals legacy.UserId
                             join supplier in suppliers on legacy.UserId equals supplier.UserId into supplierJoin
                             from supplier in supplierJoin.DefaultIfEmpty()
                             select new AdminUserRowViewModel
                             {
                                 Id = phase2.Id,
                                 Email = legacy.Email ?? "",
                                 FullName = $"{legacy.FirstName} {legacy.LastName}",
                                 Role = "OVRS_User",
                                 // Show "Active"/"Inactive" based on AccountStatus
                                 Status = phase2.AccountStatus == 1 ? "Active" : "Inactive",
                                 CompanyName = supplier?.TradingName ?? ""
                             };

                // Apply search filter (optional)
                if (!string.IsNullOrEmpty(search))
                {
                    result = result.Where(a =>
                        (a.Email != null && a.Email.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (a.FullName != null && a.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (a.CompanyName != null && a.CompanyName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    );
                }

                // Optional: filter by status dropdown if you implement one
                if (statusFilter == "Active")
                    result = result.Where(a => a.Status == "Active");
                else if (statusFilter == "Inactive")
                    result = result.Where(a => a.Status == "Inactive");

                users = result.OrderBy(a => a.FullName).ToList();
            }
            else
            {
                IQueryable<Administrator> query = _context.Administrators;

                if (roleFilter == "Administrator")
                {
                    query = query.Where(a => a.Role == "Administrator" || a.Role == "IT_Admin");
                }

                if (!string.IsNullOrEmpty(search))
                {
                    query = query.Where(a =>
                        a.Email.Contains(search) ||
                        a.FirstName.Contains(search) ||
                        a.LastName.Contains(search)
                    );
                }

                users = await query
                    .OrderBy(a => a.Id)
                    .Select(a => new AdminUserRowViewModel
                    {
                        Id = a.Id,
                        Email = a.Email,
                        FullName = $"{a.FirstName} {a.LastName}",
                        Role = a.Role,
                        Status = a.AccountStatus == 1 ? "Active" : "Inactive",
                        CompanyName = "" // Not relevant for admins
                    })
                    .ToListAsync();
            }

            ViewBag.IsOVRSUser = isOvrsUser;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_UsersTablePartial", users);
            }

            return View(users);
        }



        [HttpGet]
        public async Task<IActionResult> SearchAzureAdUsers(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 3)
            {
                return Json(new { success = false, message = "Search term must be at least 3 characters" });
            }

            try
            {
                var graphServiceClient = GetGraphServiceClient();

                // Updated syntax for Microsoft Graph SDK v5
                var users = await graphServiceClient.Users
                    .GetAsync((requestConfiguration) =>
                    {
                        requestConfiguration.QueryParameters.Filter = $"startswith(displayName,'{searchTerm}') or startswith(givenName,'{searchTerm}') or startswith(surname,'{searchTerm}') or startswith(mail,'{searchTerm}')";
                        requestConfiguration.QueryParameters.Select = new string[] { "id", "displayName", "givenName", "surname", "mail" };
                        requestConfiguration.QueryParameters.Top = 10;
                    });

                var userList = users.Value.Select(u => new
                {
                    Id = u.Id,
                    DisplayName = u.DisplayName,
                    FirstName = u.GivenName,
                    LastName = u.Surname,
                    Email = u.Mail
                }).ToList();

                return Json(new { success = true, users = userList });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error searching Azure AD users: " + ex.Message });
            }
        }

        private GraphServiceClient GetGraphServiceClient()
        {
            // Using the current Azure.Identity package instead of deprecated Microsoft.Graph.Auth
            var options = new ClientSecretCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            };

            var clientSecretCredential = new ClientSecretCredential(
                _configuration["AzureAd:TenantId"],
                _configuration["AzureAd:ClientId"],
                _configuration["AzureAd:ClientSecret"],
                options
            );

            return new GraphServiceClient(clientSecretCredential);
        }


        public class BulkDeleteUserModel
        {
            public int Id { get; set; }
            public string Type { get; set; } // "Administrator" or "OVRS_User"
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkDeleteUsers([FromBody] List<BulkDeleteUserModel> selectedUsers)
        {
            if (selectedUsers == null || !selectedUsers.Any())
            {
                return Json(new { success = false, message = "No users selected." });
            }

            try
            {
                int totalAffected = 0;

                // Handle OVRS Users
                var ovrsIds = selectedUsers.Where(x => x.Type == "OVRS_User").Select(x => x.Id).ToList();
                if (ovrsIds.Any())
                {
                    var ovrsUsers = await _context.Users
                        .Where(u => ovrsIds.Contains(u.Id) && u.Role == "OVRS_User")
                        .ToListAsync();
                    foreach (var user in ovrsUsers)
                    {
                        user.AccountStatus = 0;
                    }
                    totalAffected += ovrsUsers.Count;
                }

                // Handle Administrators
                var adminIds = selectedUsers.Where(x => x.Type == "Administrator" || x.Type == "IT_Admin").Select(x => x.Id).ToList();
                if (adminIds.Any())
                {
                    var admins = await _context.Administrators
                        .Where(a => adminIds.Contains(a.Id))
                        .ToListAsync();
                    foreach (var admin in admins)
                    {
                        admin.AccountStatus = 0;
                    }
                    totalAffected += admins.Count;
                }

                if (totalAffected > 0)
                    await _context.SaveChangesAsync();

                if (totalAffected == 0)
                    return Json(new { success = false, message = "No matching users found." });

                return Json(new { success = true, message = $"{totalAffected} user(s) set to inactive." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        public class DeleteUserModel
        {
            public int Id { get; set; }
            public string Type { get; set; } // "Administrator" or "OVRS_User"
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser([FromBody] DeleteUserModel model)
        {
            if (model == null || model.Id == 0 || string.IsNullOrEmpty(model.Type))
                return Json(new { success = false, message = "Invalid user." });

            try
            {
                if (model.Type == "OVRS_User")
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == model.Id && u.Role == "OVRS_User");
                    if (user == null)
                        return Json(new { success = false, message = "User not found." });
                    user.AccountStatus = 0;
                }
                else if (model.Type == "Administrator" || model.Type == "IT_Admin")
                {
                    var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == model.Id);
                    if (admin == null)
                        return Json(new { success = false, message = "User not found." });
                    admin.AccountStatus = 0;
                }
                else
                {
                    return Json(new { success = false, message = "Unknown user type." });
                }

                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "User deleted." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        public class ReactivateUserModel
        {
            public int Id { get; set; }
            public string Type { get; set; } // "Administrator" or "OVRS_User"
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReactivateUser([FromBody] ReactivateUserModel model)
        {
            if (model == null || model.Id == 0 || string.IsNullOrEmpty(model.Type))
                return Json(new { success = false, message = "Invalid user." });

            try
            {
                if (model.Type == "OVRS_User")
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == model.Id && u.Role == "OVRS_User");
                    if (user == null)
                        return Json(new { success = false, message = "User not found." });
                    user.AccountStatus = 1;
                }
                else if (model.Type == "Administrator" || model.Type == "IT_Admin")
                {
                    var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == model.Id);
                    if (admin == null)
                        return Json(new { success = false, message = "User not found." });
                    admin.AccountStatus = 1;
                }
                else
                {
                    return Json(new { success = false, message = "Unknown user type." });
                }

                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "User reactivated." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }


        [HttpGet]
        public async Task<IActionResult> Administrator_Profiles()
        {
            // Get the AdminId from the current user's claims
            var adminIdClaim = User.FindFirst("AdminId")?.Value;
            if (string.IsNullOrEmpty(adminIdClaim) || !int.TryParse(adminIdClaim, out int adminId))
            {
                return Unauthorized();
            }

            // Query the admin info from the DB
            var admin = await _context.Administrators
                .Where(a => a.Id == adminId)
                .Select(a => new AdministratorProfileViewModel
                {
                    Id = a.Id,
                    Email = a.Email,
                    FirstName = a.FirstName,
                    LastName = a.LastName,
                })
                .FirstOrDefaultAsync();

            if (admin == null)
            {
                return NotFound();
            }

            return View(admin);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Administrator_Profiles(AdministratorProfileViewModel model)
        {
            // Clear password-related model state if password change is not being attempted
            bool changingPassword = !string.IsNullOrWhiteSpace(model.CurrentPassword)
                || !string.IsNullOrWhiteSpace(model.NewPassword)
                || !string.IsNullOrWhiteSpace(model.ConfirmPassword);

            if (!changingPassword)
            {
                // Clear any password-related validation errors if user isn't trying to change password
                ModelState.Remove("CurrentPassword");
                ModelState.Remove("NewPassword");
                ModelState.Remove("ConfirmPassword");

                // Clear the password fields to ensure they don't hold values
                model.CurrentPassword = null;
                model.NewPassword = null;
                model.ConfirmPassword = null;
            }

            // Clear email from model state since it's handled via OTP
            ModelState.Remove("Email");
            model.Email = null;

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Get current admin
            var adminIdClaim = User.FindFirst("AdminId")?.Value;
            if (string.IsNullOrEmpty(adminIdClaim) || !int.TryParse(adminIdClaim, out int adminId))
            {
                return Unauthorized();
            }

            var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == adminId);
            if (admin == null)
            {
                ModelState.AddModelError("", "Administrator not found.");
                return View(model);
            }

            // ========== PASSWORD CHANGE LOGIC ==========
            if (changingPassword)
            {
                // 1. All fields must be filled
                if (string.IsNullOrWhiteSpace(model.CurrentPassword)
                    || string.IsNullOrWhiteSpace(model.NewPassword)
                    || string.IsNullOrWhiteSpace(model.ConfirmPassword))
                {
                    ModelState.AddModelError("", "All password fields are required.");
                    return View(model);
                }

                // 2. Check new/confirm match
                if (model.NewPassword != model.ConfirmPassword)
                {
                    ModelState.AddModelError("ConfirmPassword", "New password and confirm new password do not match.");
                    return View(model);
                }

                // 3. Check current password matches db
                string currentPasswordHash = PasswordHelper.EncryptPassword(model.CurrentPassword);
                if (!string.Equals(admin.PasswordHash, currentPasswordHash, StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("CurrentPassword", "Current password is incorrect.");
                    return View(model);
                }

                // 4. Prevent reusing the same password
                string newPasswordHash = PasswordHelper.EncryptPassword(model.NewPassword);
                if (string.Equals(currentPasswordHash, newPasswordHash, StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("NewPassword", "New password must be different from the current password.");
                    return View(model);
                }

                // 5. FIXED: Validate password requirements - DON'T add to ModelState
                // Controller: pass the attempted password to the view for modal checklist,
                // and show the modal if requirements not met

                if (!ValidatePasswordRequirements(model.NewPassword, out List<string> passwordErrors))
                {
                    ViewBag.ShowPasswordRequirementsModal = true;
                    ViewBag.AttemptedPassword = model.NewPassword; // Pass attempted password
                    return View(model);
                }

                // 6. Hash and save new password
                admin.PasswordHash = newPasswordHash;
                admin.PasswordLastUpdated = DateTime.UtcNow;
            }

            // ========== Other profile fields ==========
            admin.FirstName = model.FirstName;
            admin.LastName = model.LastName;
            // Note: Email is updated via OTP flow, not here

            await _context.SaveChangesAsync();

            TempData["ProfileUpdateSuccess"] = "Profile updated successfully.";

            // Clear password fields before redirect
            model.CurrentPassword = null;
            model.NewPassword = null;
            model.ConfirmPassword = null;

            return RedirectToAction(nameof(Administrator_Profiles));
        }


        // Step 1: Send Email OTP
        // Enhanced SendAdminEmailOtp method with session-based rate limiting
        [HttpPost]
        public async Task<IActionResult> SendAdminEmailOtp([FromBody] string newEmail)
        {
            if (string.IsNullOrWhiteSpace(newEmail))
                return BadRequest(new { success = false, message = "Email is required" });

            // Get email validation service
            var emailValidationService = HttpContext.RequestServices.GetRequiredService<EmailValidationService>();

            // Comprehensive email validation
            var validationResult = await emailValidationService.ValidateEmailAsync(newEmail);
            if (!validationResult.IsValid)
            {
                return BadRequest(new { success = false, message = validationResult.ErrorMessage });
            }

            // Get logged in admin
            var adminIdClaim = User.FindFirst("AdminId")?.Value;
            if (string.IsNullOrEmpty(adminIdClaim) || !int.TryParse(adminIdClaim, out int adminId))
                return Unauthorized();

            var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == adminId);
            if (admin == null) return Unauthorized();

            // --- Block if changing to own current email ---
            if (admin.Email != null && admin.Email.Trim().ToLower() == newEmail.Trim().ToLower())
            {
                return BadRequest(new { success = false, message = "You are already using this email address." });
            }

            // Check if email is already in use by another admin
            var existingAdmin = await _context.Administrators
                .FirstOrDefaultAsync(a => a.Email.ToLower() == newEmail.ToLower() && a.Id != adminId);

            if (existingAdmin != null)
                return BadRequest(new { success = false, message = "This email address is already in use by another administrator." });

            // Enhanced Rate Limiting Logic
            var now = DateTime.UtcNow;

            // Check if user is currently blocked
            if (admin.OtpBlockedUntil.HasValue && now < admin.OtpBlockedUntil.Value)
            {
                var timeLeft = admin.OtpBlockedUntil.Value - now;
                var minutesLeft = Math.Ceiling(timeLeft.TotalMinutes);
                return BadRequest(new
                {
                    success = false,
                    message = $"Too many OTP requests. Please wait {minutesLeft} minutes before trying again.",
                    secondsLeft = (int)timeLeft.TotalSeconds,
                    isBlocked = true
                });
            }

            // Reset count if enough time has passed since last request (e.g., 1 hour)
            if (admin.LastOtpRequestTime.HasValue &&
                now.Subtract(admin.LastOtpRequestTime.Value).TotalHours >= 1)
            {
                admin.OtpRequestCount = 0;
                admin.OtpBlockedUntil = null;
            }

            // Check basic cooldown (60 seconds between requests)
            if (admin.LastOtpRequestTime.HasValue &&
                now.Subtract(admin.LastOtpRequestTime.Value).TotalSeconds < 60)
            {
                var timeLeft = 60 - (int)now.Subtract(admin.LastOtpRequestTime.Value).TotalSeconds;
                return BadRequest(new
                {
                    success = false,
                    message = $"Please wait {timeLeft} seconds before requesting another code.",
                    secondsLeft = timeLeft
                });
            }

            // Check attempt limit
            if (admin.OtpRequestCount >= 3)
            {
                // Block for 15 minutes after 3 attempts
                admin.OtpBlockedUntil = now.AddMinutes(15);
                admin.OtpRequestCount = 0; // Reset for next cycle
                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message = "Too many OTP requests. You are blocked for 15 minutes.",
                    secondsLeft = 15 * 60,
                    isBlocked = true
                });
            }

            // Generate OTP
            var otpService = HttpContext.RequestServices.GetRequiredService<OtpService>();
            var otpCode = otpService.GenerateOtpCode();
            var expiry = otpService.GetOtpExpiration();

            // Update rate limiting counters
            admin.OtpRequestCount++;
            admin.LastOtpRequestTime = now;

            // Save OTP + pending email
            admin.OtpCode = otpCode;
            admin.OtpExpiration = expiry;
            admin.PendingEmail = newEmail;
            admin.OtpType = "email";

            await _context.SaveChangesAsync();

            // Send OTP email with error handling
            var emailService = HttpContext.RequestServices.GetRequiredService<PhoneOtpEmailService>();
            var emailResult = await emailService.SendEmailOtpAsync(newEmail, $"{admin.FirstName} {admin.LastName}", otpCode);

            if (!emailResult.Success)
            {
                // Clear the OTP data since email failed, but keep rate limiting counters
                admin.OtpCode = null;
                admin.OtpExpiration = null;
                admin.PendingEmail = null;
                admin.OtpType = null;
                // Don't reset LastOtpRequestTime and OtpRequestCount to maintain rate limiting
                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message = "Failed to send verification email. Please check the email address and try again."
                });
            }

            // Calculate remaining attempts
            int remainingAttempts = 3 - admin.OtpRequestCount;

            return Ok(new
            {
                success = true,
                message = "OTP sent successfully",
                remainingAttempts = remainingAttempts,
                attemptsUsed = admin.OtpRequestCount
            });
        }

        // Optional: Add a method to reset rate limiting (for admin use or after successful verification)
        private async Task ResetOtpRateLimiting(Administrator admin)
        {
            admin.OtpRequestCount = 0;
            admin.OtpBlockedUntil = null;
            admin.LastOtpRequestTime = null;
            await _context.SaveChangesAsync();
        }

        // Step 2: Verify OTP and update email
        // Update VerifyAdminEmailOtp to reset rate limiting on successful verification
        [HttpPost]
        public async Task<IActionResult> VerifyAdminEmailOtp([FromBody] VerifyAdminOtpRequest request)
        {
            if (string.IsNullOrEmpty(request?.Otp))
                return BadRequest(new { success = false, message = "OTP is required" });

            var adminIdClaim = User.FindFirst("AdminId")?.Value;
            if (string.IsNullOrEmpty(adminIdClaim) || !int.TryParse(adminIdClaim, out int adminId))
                return Unauthorized();

            var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == adminId);
            if (admin == null) return Unauthorized();

            var otpService = HttpContext.RequestServices.GetRequiredService<OtpService>();
            bool valid = otpService.ValidateOtp(admin.OtpCode, admin.OtpExpiration, request.Otp);
            if (!valid)
                return BadRequest(new { success = false, message = "Invalid or expired OTP" });

            // Update email and reset rate limiting on successful verification
            if (!string.IsNullOrEmpty(admin.PendingEmail))
            {
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    admin.Email = admin.PendingEmail;
                    admin.PendingEmail = null;
                    admin.OtpCode = null;
                    admin.OtpExpiration = null;
                    admin.OtpType = null;

                    // Reset rate limiting on successful verification
                    admin.OtpRequestCount = 0;
                    admin.OtpBlockedUntil = null;
                    admin.LastOtpRequestTime = null;

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            return Ok(new { success = true, message = "Email updated successfully" });
        }
        // ----------------------------------------------------------------------------------------------------------------------------------------------


        public class DeleteDraftDocumentRequest
        {
            public int DocumentId { get; set; }
        }

        public class DeleteDraftReq
        {
            public int Id { get; set; }
        }

        // Password validation method (SAME AS OVRS)
        private bool ValidatePasswordRequirements(string password, out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(password))
            {
                errors.Add("Password is required.");
                return false;
            }

            if (password.Length < 8 || password.Length > 15)
            {
                errors.Add("Password must be 8 to 15 characters long.");
            }

            if (!password.Any(char.IsLower))
            {
                errors.Add("Password must contain a lowercase letter.");
            }

            if (!password.Any(char.IsUpper))
            {
                errors.Add("Password must contain an uppercase letter.");
            }

            if (!password.Any(char.IsDigit))
            {
                errors.Add("Password must contain a number.");
            }

            if (!password.Any(c => "!@#$%^&*()_+-=[]{}|;:,.<>?".Contains(c)))
            {
                errors.Add("Password must contain a special character.");
            }

            return errors.Count == 0;
        }
    }
}