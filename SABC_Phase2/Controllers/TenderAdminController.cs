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
using SABC_Phase2.Models.Security;

namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for administering tender creation and listing.
    /// Supports both immediate and scheduled publishing via Hangfire and Azure Blob Storage for document uploads.
    /// </summary>
    public class TenderAdminController : Controller
    {
        private readonly AuditLogService _auditLogService;
        private readonly Phase2Context _context;
        private readonly LegacyDbContext _legacyContext;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly SouthAfricanTimeService _saTimeService;
        private readonly TenderReportPdfService _pdfService;
        private readonly EmailService _emailService;
        private readonly ISecurityService _securityService;
        private readonly ILogger<TenderAdminController> _logger; // ✅ add logger
        private readonly AuditLogPdfService _auditLogPdfService;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        public TenderAdminController(
            Phase2Context context,
            LegacyDbContext legacyContext,
            IConfiguration configuration,
            IWebHostEnvironment env,
            TenderReportPdfService pdfService,
            SouthAfricanTimeService saTimeService,
            AuditLogService auditLogService,
            EmailService emailService,
            ISecurityService securityService,
            ILogger<TenderAdminController> logger,
            AuditLogPdfService auditLogPdfService) // ✅ inject logger
        {
            _context = context;
            _legacyContext = legacyContext;
            _configuration = configuration;
            _env = env;
            _pdfService = pdfService;
            _saTimeService = saTimeService;
            _auditLogService = auditLogService;
            _emailService = emailService;
            _securityService = securityService;
            _logger = logger; // ✅ assign logger
            _auditLogPdfService = auditLogPdfService;
        }

        // Helper to get current admin info
        private async Task<(int adminId, string adminEmail, string adminFullName, string adminRole)> GetCurrentAdminAsync()
        {
            var idStr = User.FindFirst("UserId")?.Value ?? User.FindFirst("AdminId")?.Value;
            int adminId = 0;
            int.TryParse(idStr, out adminId);

            var email = User.FindFirst("AdminEmail")?.Value
                ?? User.FindFirst(ClaimTypes.Email)?.Value
                ?? User.Identity?.Name;

            string fullName = email; // fallback
            string role = "Unknown"; // fallback

            if (adminId > 0)
            {
                var admin = await _context.Administrators.FindAsync(adminId);
                if (admin != null)
                {
                    if (!string.IsNullOrWhiteSpace(admin.FirstName) && !string.IsNullOrWhiteSpace(admin.LastName))
                    {
                        fullName = $"{admin.FirstName} {admin.LastName}";
                    }
                    if (!string.IsNullOrWhiteSpace(admin.Role))
                    {
                        role = admin.Role;
                    }
                }
            }
            return (adminId, email, fullName, role);
        }


        // -----------------------------------------------------------------------------------------------------------------------------------------
        // CREATE TENDER LOGIC

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
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();

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

                    // --- AUDIT LOG: Log scheduled tender creation WITH ROLE ---
                    var sastime = _saTimeService.GetCurrentSouthAfricanTime();
                    var timestamp = sastime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                    await _auditLogService.LogAsync(
                        adminId,
                        adminEmail,
                        adminFullName,
                        adminRole, // <--- Pass the role here
                        "ScheduleTender",
                        $"Scheduled Tender \"{scheduledTender.TenderNumber}\" was scheduled"
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

            // --- AUDIT LOG: Log immediate tender creation WITH ROLE ---
            var timestamp2 = saNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole, // <--- Pass the role here
                "CreateTender",
                $"Tender \"{tender.TenderNumber}\" was published"
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
               adminRole, // <--- Pass the role here
               "DeleteDraftAfterPublish",
               $"Draft for tender \"{tender.TenderNumber}\" was published"
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
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
            string actionType = isNewDraft ? "CreateDraft" : "EditDraft";
            string description = isNewDraft
                ? $"Draft for Tender \"{draft.TenderNumber}\" was created"
                : $"Draft for Tender \"{draft.TenderNumber}\" was edited";
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole,
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
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "DeleteDraft",
                    $"Draft for Tender \"{draft.TenderNumber}\" was deleted"
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


        // -----------------------------------------------------------------------------------------------------------------------------------------
        // INDEX Pages
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

       


        // -------------------------------------------------------------------------------------------------------------------------
        // Edit a Published Tender Logic

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

     
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, TenderEditDto dto)
        {
            // --- TENDER NUMBER UNIQUENESS VALIDATION ON EDIT ---
            if (!string.IsNullOrWhiteSpace(dto.TenderNumber))
            {
                var tenderNumber = dto.TenderNumber.Trim();

                bool existsInOtherPublished = await _context.Tenders
                    .AnyAsync(t => t.TenderNumber == tenderNumber && t.Id != id);

                bool existsInDraft = await _context.TenderAdminsDraft
                    .AnyAsync(d => d.TenderNumber == tenderNumber);

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

            // ✅ CAPTURE ORIGINAL STATE BEFORE ANY CHANGES
            var originalTender = new Tender
            {
                TenderType = tender.TenderType,
                TenderNumber = tender.TenderNumber,
                Title = tender.Title,
                Description = tender.Description,
                ClosingDate = tender.ClosingDate,
                ClosingTime = tender.ClosingTime,
                Status = tender.Status,
                AwardedTender = tender.AwardedTender != null ? new AwardedTender
                {
                    AwardedCompanyName = tender.AwardedTender.AwardedCompanyName
                } : null
            };

            // ✅ TRACK DOCUMENT CHANGES
            var originalDocs = tender.Documents.ToList();
            var originalAwardedDocs = tender.AwardedTender?.Documents?.ToList() ?? new List<TenderDocument>();

            // Also get previously uploaded awarded docs
            var previousAwardedDocs = await _context.TenderDocuments
                .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null)
                .ToListAsync();
            originalAwardedDocs.AddRange(previousAwardedDocs.Where(d => !originalAwardedDocs.Any(oad => oad.Id == d.Id)));

            // --- EARLY CHECK: Prevent awarding to deleted company BEFORE ANY DATA CHANGES ---
            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                var supplier = await _legacyContext.TblSuppliers
                    .FirstOrDefaultAsync(s => s.TradingName == dto.AwardedTender || s.LegalName == dto.AwardedTender);

                if (supplier != null)
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.LegacyUserId == supplier.UserId);

                    if (user != null && user.AccountStatus == 0)
                    {
                        return Json(new
                        {
                            success = false,
                            deletedAccount = true,
                            companyName = supplier.TradingName ?? supplier.LegalName
                        });
                    }
                }
            }

            // --- AWARDED DOCUMENT VALIDATION ---
            if (dto.Status == "Awarded Tender")
            {
                var awardedDocsToDelete = dto.AwardedDocumentsToDelete ?? new List<int>();

                int remainingAwardedDocsFromCurrent = tender.AwardedTender?.Documents
                    .Where(d => !awardedDocsToDelete.Contains(d.Id))
                    .Count() ?? 0;

                int remainingPreviousAwardedDocs = 0;
                if (tender.AwardedTender == null)
                {
                    var previousDocs = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && !awardedDocsToDelete.Contains(doc.Id))
                        .CountAsync();
                    remainingPreviousAwardedDocs = previousDocs;
                }

                int totalExistingAwardedDocs = remainingAwardedDocsFromCurrent + remainingPreviousAwardedDocs;
                int newAwardedUploads = dto.UploadedFiles?.Count ?? 0;

                if ((totalExistingAwardedDocs + newAwardedUploads) == 0)
                {
                    ModelState.AddModelError("", "You must upload at least one awarded tender document.");

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

            // Handle awarded document deletions
            if (dto.AwardedDocumentsToDelete != null && dto.AwardedDocumentsToDelete.Any())
            {
                var docsToRemove = new List<TenderDocument>();

                if (tender.AwardedTender != null)
                {
                    docsToRemove.AddRange(tender.AwardedTender.Documents.Where(d => dto.AwardedDocumentsToDelete.Contains(d.Id)));
                }

                var previousAwardedDocsToDelete = await _context.TenderDocuments
                    .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && dto.AwardedDocumentsToDelete.Contains(doc.Id))
                    .ToListAsync();
                docsToRemove.AddRange(previousAwardedDocsToDelete);

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

                    var previousAwardedDocsForRename = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null)
                        .ToListAsync();

                    foreach (var doc in previousAwardedDocsForRename)
                    {
                        if (!string.IsNullOrEmpty(doc.SharePointPath) && doc.SharePointPath.Contains(oldTenderNumber))
                        {
                            doc.SharePointPath = doc.SharePointPath.Replace(oldTenderNumber, newTenderNumber);
                        }
                    }
                }
                catch (Exception ex)
                {
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

            // --- AwardedTender logic ---
            if (dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender))
            {
                if (tender.AwardedTender == null)
                {
                    var awardedTender = new AwardedTender
                    {
                        AwardedCompanyName = dto.AwardedTender,
                        TenderId = tender.Id
                    };
                    _context.AwardedTenders.Add(awardedTender);
                    await _context.SaveChangesAsync();
                    tender.AwardedTenderId = awardedTender.Id;

                    var previousAwardedDocsForReassign = await _context.TenderDocuments
                        .Where(doc => doc.TenderId == tender.Id && doc.AwardedTenderId != null && doc.AwardedTenderId != awardedTender.Id)
                        .ToListAsync();

                    foreach (var doc in previousAwardedDocsForReassign)
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

            // ✅ BUILD DETAILED AUDIT LOG
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();

            // Track all document changes
            var documentChanges = new List<string>();

            // Track supporting document changes
            var supportingDocChanges = _auditLogService.TrackDocumentChanges(
                originalDocs,
                dto.DocumentsToDelete,
                dto.Status != "Awarded Tender" ? dto.UploadedFiles?.ToList() : null,
                isAwardedDocs: false
            );
            documentChanges.AddRange(supportingDocChanges);

            // Track awarded document changes
            var awardedDocChanges = _auditLogService.TrackDocumentChanges(
                originalAwardedDocs,
                dto.AwardedDocumentsToDelete,
                dto.Status == "Awarded Tender" ? dto.UploadedFiles?.ToList() : null,
                isAwardedDocs: true
            );
            documentChanges.AddRange(awardedDocChanges);

            // Build comprehensive change log with admin name
            string changeDescription = _auditLogService.BuildTenderChangeLog(
                originalTender,
                dto,
                adminFullName,
                documentChanges
            );

            // Save changes to database
            await _context.SaveChangesAsync();

            // Log the detailed changes
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole,
                "EditTender",
                changeDescription
            );

            bool isAwarded = dto.Status == "Awarded Tender" && !string.IsNullOrWhiteSpace(dto.AwardedTender);

            return Json(new
            {
                success = true,
                tenderNumber = tender.TenderNumber,
                redirectUrl = Url.Action("Index", "TenderAdmin"),
                awarded = isAwarded
            });
        }

        // ----------------------------------------------------------------------------------------------------------------------------------------------
        // SCHEDULED Tender Logic

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

            // ✅ CAPTURE ORIGINAL STATE BEFORE ANY CHANGES
            var originalScheduledTender = new ScheduledTender
            {
                TenderType = scheduledTender.TenderType,
                TenderNumber = scheduledTender.TenderNumber,
                Title = scheduledTender.Title,
                Description = scheduledTender.Description,
                ClosingDate = scheduledTender.ClosingDate,
                ClosingTime = scheduledTender.ClosingTime,
                Status = scheduledTender.Status,
                ScheduledPublishDateTime = scheduledTender.ScheduledPublishDateTime
            };

            // ✅ TRACK DOCUMENT CHANGES
            var originalDocs = scheduledTender.Documents.ToList();

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

            // ✅ BUILD DETAILED AUDIT LOG
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();

            // Track all document changes
            var documentChanges = new List<string>();

            // Track document deletions
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var deletedFiles = originalDocs
                    .Where(d => dto.DocumentsToDelete.Contains(d.Id))
                    .Select(d => d.FileName)
                    .ToList();

                if (deletedFiles.Any())
                {
                    documentChanges.Add($"Documents Deleted: {string.Join(", ", deletedFiles)}");
                }
            }

            // Track document additions
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                var newFileNames = dto.UploadedFiles.Select(f => f.FileName).ToList();
                documentChanges.Add($"Documents Added: {string.Join(", ", newFileNames)}");
            }

            // Build comprehensive change log
            string changeDescription = BuildScheduledTenderChangeLog(
                originalScheduledTender,
                scheduledTender,
                dto,
                adminFullName,
                documentChanges
            );

            // Save changes to database
            await _context.SaveChangesAsync();

            // Log the detailed changes
            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole,
                "EditScheduledTender",
                changeDescription
            );

            return Json(new
            {
                success = true,
                tenderNumber = scheduledTender.TenderNumber,
                redirectUrl = Url.Action("ScheduledIndex", "TenderAdmin")
            });
        }

        /// <summary>
        /// Helper method to build detailed change log for scheduled tenders
        /// </summary>
        private string BuildScheduledTenderChangeLog(
            ScheduledTender originalTender,
            ScheduledTender updatedTender,
            TenderEditDto dto,
            string adminFullName,
            List<string> documentChanges)
        {
            var changes = new List<string>();

            // Track tender type changes
            if (originalTender.TenderType != updatedTender.TenderType)
            {
                changes.Add($"Tender Type: '{originalTender.TenderType}' → '{updatedTender.TenderType}'");
            }

            // Track tender number changes
            if (originalTender.TenderNumber != updatedTender.TenderNumber)
            {
                changes.Add($"Tender Number: '{originalTender.TenderNumber}' → '{updatedTender.TenderNumber}'");
            }

            // Track title changes
            if (originalTender.Title != updatedTender.Title)
            {
                changes.Add($"Title: '{originalTender.Title}' → '{updatedTender.Title}'");
            }

            // Track description changes
            if (originalTender.Description != updatedTender.Description)
            {
                var oldDesc = originalTender.Description?.Length > 50
                    ? originalTender.Description.Substring(0, 50) + "..."
                    : originalTender.Description;
                var newDesc = updatedTender.Description?.Length > 50
                    ? updatedTender.Description.Substring(0, 50) + "..."
                    : updatedTender.Description;
                changes.Add($"Description: '{oldDesc}' → '{newDesc}'");
            }

            // Track closing date changes
            if (originalTender.ClosingDate != updatedTender.ClosingDate)
            {
                changes.Add($"Closing Date: {originalTender.ClosingDate:yyyy-MM-dd} → {updatedTender.ClosingDate:yyyy-MM-dd}");
            }

            // Track closing time changes
            if (originalTender.ClosingTime != updatedTender.ClosingTime)
            {
                changes.Add($"Closing Time: '{originalTender.ClosingTime}' → '{updatedTender.ClosingTime}'");
            }

            // Track status changes
            if (originalTender.Status != updatedTender.Status)
            {
                changes.Add($"Status: '{originalTender.Status}' → '{updatedTender.Status}'");
            }

            // Track scheduled publish date/time changes
            if (originalTender.ScheduledPublishDateTime != updatedTender.ScheduledPublishDateTime)
            {
                // Convert UTC to SAST for display
                var oldSaTime = _saTimeService.ConvertUtcToSaLocal(originalTender.ScheduledPublishDateTime);
                var newSaTime = _saTimeService.ConvertUtcToSaLocal(updatedTender.ScheduledPublishDateTime);

                changes.Add($"Scheduled Publish: {oldSaTime:yyyy-MM-dd HH:mm} SAST → {newSaTime:yyyy-MM-dd HH:mm} SAST");
            }

            // Add document changes if provided
            if (documentChanges != null && documentChanges.Any())
            {
                changes.AddRange(documentChanges);
            }

            // Get current South African time
            var saTime = _saTimeService.GetCurrentSouthAfricanTime();
            var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            // Build final description with admin name and timestamp
            if (changes.Any())
            {
                return $"Scheduled Tender '{updatedTender.TenderNumber}' modified {string.Join("; ", changes)}";
            }
            else
            {
                return $"Scheduled Tender '{updatedTender.TenderNumber}' was accessed by {adminFullName} on {timestamp} SAST but no changes were detected";
            }
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

                // --- AUDIT LOG: Log scheduled tender deletion with detailed info and timestamp ---
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                var saTime = _saTimeService.GetCurrentSouthAfricanTime();
                var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                string deletedDocs = (scheduledTender.Documents != null && scheduledTender.Documents.Any())
                    ? $"Documents deleted: {string.Join(", ", scheduledTender.Documents.Select(d => d.FileName))}"
                    : "No documents were associated with this scheduled tender.";

                string changeDescription = $"Scheduled Tender \"{scheduledTender.TenderNumber}\" was deleted by {adminFullName} ({adminEmail}) on {timestamp} SAST.";

                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "DeleteScheduledTender",
                    changeDescription
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
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
            // Get South African time stamp
            var saTime = _saTimeService.GetCurrentSouthAfricanTime();
            var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole,
                "GenerateTenderSupplierReport",
                $"Supplier report for Tender \"{tender.TenderNumber}\" was generated"
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
            var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
            var saTime = _saTimeService.GetCurrentSouthAfricanTime();
            var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            await _auditLogService.LogAsync(
                adminId,
                adminEmail,
                adminFullName,
                adminRole,
                "GenerateClosedTendersSummaryReport",
                $"Closed Tenders Summary report ({startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}) was generated"
            );

            // Return the PDF file as a download, naming it with the date range
            return File(pdfBytes, "application/pdf", $"ClosedTendersSummary_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.pdf");
        }







        // ----------------------------------------------------------------------------------------------------------------------------------------------
        // USER MANAGEMENT SECTION (IT ADMIN)

        [HttpGet]
        public async Task<IActionResult> Audit_Logs()
        {
            // Optionally, you can pass audit logs to the view for display
            var auditLogs = await _context.AuditLogs
                .OrderByDescending(a => a.Timestamp)
                .Take(100) // Show last 100 for performance
                .ToListAsync();

            return View(auditLogs);
        }

        [HttpPost]
        public async Task<IActionResult> GenerateAuditLogReport(int? dateRange, string fromDate, string toDate)
        {
            try
            {
                DateTime? startDate = null;
                DateTime? endDate = null;

                // Handle predefined date ranges
                if (dateRange.HasValue)
                {
                    endDate = DateTime.Now;
                    startDate = endDate.Value.AddDays(-dateRange.Value);
                }
                // Handle custom date range
                else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
                {
                    if (DateTime.TryParse(fromDate, out DateTime parsedFromDate) &&
                        DateTime.TryParse(toDate, out DateTime parsedToDate))
                    {
                        startDate = parsedFromDate;
                        endDate = parsedToDate.Date.AddDays(1).AddSeconds(-1); // End of day
                    }
                    else
                    {
                        return BadRequest("Invalid date format");
                    }
                }
                else
                {
                    return BadRequest("Please select a date range or specify custom dates");
                }

                // Fetch audit logs from database
                var query = _context.AuditLogs.AsQueryable();

                if (startDate.HasValue && endDate.HasValue)
                {
                    query = query.Where(a => a.Timestamp >= startDate.Value && a.Timestamp <= endDate.Value);
                }

                var auditLogs = await query
                    .OrderByDescending(a => a.Timestamp)
                    .ToListAsync();

                // Generate PDF
                var pdfBytes = _auditLogPdfService.GenerateAuditLogReport(auditLogs, startDate, endDate);

                if (pdfBytes == null || pdfBytes.Length == 0)
                {
                    return StatusCode(500, "Failed to generate PDF");
                }

                // Return PDF file
                var fileName = $"AuditLogReport_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Error generating audit report: {ex.Message}");
                return StatusCode(500, $"An error occurred while generating the report: {ex.Message}");
            }
        }

        // Helper method to check if method exists
        private bool HasMethod(string methodName)
        {
            return this.GetType().GetMethod(methodName) != null;
        }

        [HttpGet]
        public async Task<IActionResult> Users_Management(string search = "",string roleFilter = "all",string statusFilter = "all",int page = 1,int pageSize = 7)
        {
            bool isOvrsUser = roleFilter == "OVRS_User";
            IEnumerable<AdminUserRowViewModel> result;

            if (isOvrsUser)
            {
                // PHASE 2: Get *ALL* OVRS users (active or inactive)
                var ovrsPhase2Users = await _context.Users
                    .Where(u => u.Role == "OVRS_User" && u.LegacyUserId != null)
                    .ToListAsync();

                var legacyIds = ovrsPhase2Users.Select(u => u.LegacyUserId.Value).ToList();
                var legacyUsers = await _legacyContext.TblUsers.ToListAsync();
                var suppliers = await _legacyContext.TblSuppliers.ToListAsync();

                result = from phase2 in ovrsPhase2Users
                         join legacy in legacyUsers on phase2.LegacyUserId equals legacy.UserId
                         join supplier in suppliers on legacy.UserId equals supplier.UserId into supplierJoin
                         from supplier in supplierJoin.DefaultIfEmpty()
                         select new AdminUserRowViewModel
                         {
                             Id = phase2.Id,
                             Email = legacy.Email ?? "",
                             FullName = $"{legacy.FirstName} {legacy.LastName}",
                             Role = "OVRS_User",
                             Status = phase2.AccountStatus == 1 ? "Active" : "Inactive",
                             CompanyName = supplier?.TradingName ?? ""
                         };

                // Search filter
                if (!string.IsNullOrEmpty(search))
                {
                    result = result.Where(a =>
                        (a.Email != null && a.Email.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (a.FullName != null && a.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                        (a.CompanyName != null && a.CompanyName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    );
                }

                // Status filter
                if (statusFilter == "Active")
                    result = result.Where(a => a.Status == "Active");
                else if (statusFilter == "Inactive")
                    result = result.Where(a => a.Status == "Inactive");
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

                if (statusFilter == "Active")
                    query = query.Where(a => a.AccountStatus == 1);
                else if (statusFilter == "Inactive")
                    query = query.Where(a => a.AccountStatus == 0);

                result = await query
                    .OrderBy(a => a.Id)
                    .Select(a => new AdminUserRowViewModel
                    {
                        Id = a.Id,
                        Email = a.Email,
                        FullName = $"{a.FirstName} {a.LastName}",
                        Role = a.Role,
                        Status = a.AccountStatus == 1 ? "Active" : "Inactive",
                        CompanyName = ""
                    })
                    .ToListAsync();
            }

            // Pagination logic
            int totalItems = result.Count();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            var pagedUsers = result
                .OrderBy(u => u.FullName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // Set ViewBag values for pagination and filters
            ViewBag.IsOVRSUser = isOvrsUser;
            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;
            ViewBag.Search = search;
            ViewBag.RoleFilter = roleFilter;
            ViewBag.StatusFilter = statusFilter;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_UsersTablePartial", pagedUsers);
            }

            return View(pagedUsers);
        }

        // Add a new endpoint to generate secure form data
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult GenerateSecureFormData([FromBody] AzureUserValidationData userData)
        {
            try
            {
                var nonce = _securityService.GenerateNonce();
                var timestamp = _securityService.GetCurrentTimestamp();

                var hash = _securityService.GenerateDataHash(
                    userData.FirstName,
                    userData.LastName,
                    userData.Email,
                    userData.Id,
                    nonce,
                    timestamp
                );

                return Json(new
                {
                    success = true,
                    hash = hash,
                    nonce = nonce,
                    timestamp = timestamp
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error generating secure form data");
                return Json(new { success = false, message = "Security initialization failed" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateUser([FromBody] SecureCreateUserRequest request)
        {
            try
            {
                // 🔒 IMMEDIATE TAMPER CHECK
                if (string.IsNullOrEmpty(request.DataHash) ||
                    string.IsNullOrEmpty(request.Nonce) ||
                    request.Timestamp == 0)
                {
                    _logger?.LogWarning("Missing security data in request");
                    return Json(new
                    {
                        success = false,
                        tampered = true,
                        tamperType = "MISSING_SECURITY_DATA",
                        message = "Security validation data is missing"
                    });
                }

                // 🔒 ANTI-TAMPER VALIDATION
                var tamperResult = _securityService.ValidateFormIntegrity(request, "your-secret-key");
                if (tamperResult.IsTampered)
                {
                    _logger?.LogWarning("Form tampering detected: {TamperType} - {Details}",
                        tamperResult.TamperType, tamperResult.Details);

                    return Json(new
                    {
                        success = false,
                        tampered = true,
                        tamperType = tamperResult.TamperType,
                        message = tamperResult.Message
                    });
                }

                // 🔒 AZURE AD VALIDATION
                var graphClient = GetGraphServiceClient();
                var azureValidation = await _securityService.ValidateAzureUserData(
                    request.AzureAdId,
                    request.FirstName,
                    request.LastName,
                    request.Email,
                    graphClient);

                if (azureValidation.IsTampered)
                {
                    _logger?.LogWarning("Azure AD validation failed: {TamperType} - {Details}",
                        azureValidation.TamperType, azureValidation.Details);

                    return Json(new
                    {
                        success = false,
                        tampered = true,
                        tamperType = azureValidation.TamperType,
                        message = azureValidation.Message
                    });
                }

                // Validate input
                if (string.IsNullOrWhiteSpace(request.FirstName) ||
                    string.IsNullOrWhiteSpace(request.LastName) ||
                    string.IsNullOrWhiteSpace(request.Email) ||
                    string.IsNullOrWhiteSpace(request.Role) ||
                    string.IsNullOrWhiteSpace(request.Password))
                {
                    return Json(new { success = false, message = "All fields are required." });
                }

                // Validate email format
                if (!IsValidEmail(request.Email))
                {
                    return Json(new { success = false, message = "Invalid email format." });
                }

                // Check if email already exists
                var existingUser = await _context.Administrators
                    .FirstOrDefaultAsync(a => a.Email.ToLower() == request.Email.ToLower());

                if (existingUser != null)
                {
                    return Json(new { success = false, message = "A user with this email already exists." });
                }

                // Validate role
                var validRoles = new[] { "IT_Admin", "Tender_Administrator", "Vendor_Administrator" };
                if (!validRoles.Contains(request.Role))
                {
                    return Json(new { success = false, message = "Invalid role selected." });
                }

                // Store the plain password for email before hashing
                string plainPassword = request.Password;

                // Hash the password
                string hashedPassword;
                try
                {
                    hashedPassword = PasswordHelper.EncryptPassword(request.Password);
                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = "Error processing password." });
                }

                // Get current South African time
                var saTimeService = new SouthAfricanTimeService();
                var currentSaTime = saTimeService.GetCurrentSouthAfricanTime();

                // Create new administrator
                var newAdmin = new Administrator
                {
                    Email = request.Email.Trim(),
                    PasswordHash = hashedPassword,
                    FirstName = request.FirstName.Trim(),
                    LastName = request.LastName.Trim(),
                    CreatedAt = currentSaTime.ToDateTimeUnspecified(),
                    Role = request.Role,
                    AccountStatus = 1,
                    OtpCode = null,
                    OtpExpiration = null,
                    PendingEmail = null,
                    OtpType = null,
                    LastOtpRequestTime = null,
                    PasswordLastUpdated = null,
                    OtpBlockedUntil = null,
                    OtpRequestCount = 0
                };

                // Save to database
                _context.Administrators.Add(newAdmin);
                await _context.SaveChangesAsync();

                // --- AUDIT LOG: Log admin who created the user, with date and time ---
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                var timestamp = currentSaTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                string changeDescription = $"User account for \"{newAdmin.FirstName} {newAdmin.LastName}\" ({newAdmin.Email}) with role \"{newAdmin.Role}\" was created";

                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "CreateUser",
                    changeDescription
                );

                // Send welcome email with account details
                try
                {
                    await _emailService.SendNewUserAccountEmailAsync(
                        newAdmin.Email,
                        newAdmin.FirstName,
                        newAdmin.LastName,
                        newAdmin.Role,
                        plainPassword
                    );
                }
                catch (Exception emailEx)
                {
                    Console.WriteLine($"Failed to send welcome email: {emailEx.Message}");
                    return Json(new
                    {
                        success = true,
                        message = "User account created successfully! However, there was an issue sending the welcome email. Please manually provide the user with their login credentials.",
                        userId = newAdmin.Id,
                        emailWarning = true
                    });
                }

                return Json(new
                {
                    success = true,
                    message = "User account created successfully! A welcome email with login credentials has been sent to the user.",
                    userId = newAdmin.Id
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating user");
                return Json(new
                {
                    success = false,
                    message = "An error occurred while creating the user account. Please try again."
                });
            }
        }
        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        public class CreateUserRequest
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public string Role { get; set; }
            public string Password { get; set; }
            public string AzureAdId { get; set; } // Optional if you want to store Azure AD reference
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
                        requestConfiguration.QueryParameters.Filter = $"startswith(displayName,'{searchTerm}') or startswith(givenName,'{searchTerm}') or startswith(surname,'{searchTerm}') or startswith(mail,'{searchTerm}') or startswith(userPrincipalName,'{searchTerm}')";
                        requestConfiguration.QueryParameters.Select = new string[] { "id", "displayName", "givenName", "surname", "mail", "userPrincipalName" };
                        requestConfiguration.QueryParameters.Top = 10;
                    });

                var userList = users.Value.Select(u => {
                    var displayName = u.DisplayName ?? $"{u.GivenName ?? ""} {u.Surname ?? ""}".Trim();
                    var email = u.Mail ?? u.UserPrincipalName ?? "";

                    // Skip users with no meaningful display name
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim() == "")
                    {
                        displayName = email; // Use email as fallback display name
                    }

                    return new
                    {
                        Id = u.Id ?? "",
                        DisplayName = displayName,
                        FirstName = u.GivenName ?? "",
                        LastName = u.Surname ?? "",
                        Email = email
                    };
                })
                .Where(u => !string.IsNullOrWhiteSpace(u.DisplayName) && !string.IsNullOrWhiteSpace(u.Email))
                .ToList();

                Console.WriteLine($"Found {userList.Count} users matching '{searchTerm}'");
                foreach (var user in userList.Take(3)) // Log first 3 for debugging
                {
                    Console.WriteLine($"User: {user.DisplayName}, Email: {user.Email}");
                }

                return Json(new { success = true, users = userList });
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging
                Console.WriteLine($"Error searching Azure AD: {ex}");
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
                List<string> deletedUserSummaries = new List<string>();

                // Handle OVRS Users
                var ovrsIds = selectedUsers.Where(x => x.Type == "OVRS_User").Select(x => x.Id).ToList();
                if (ovrsIds.Any())
                {
                    var ovrsUsers = await _context.Users
                        .Where(u => ovrsIds.Contains(u.Id) && u.Role == "OVRS_User")
                        .ToListAsync();

                    // For each OVRS_User, get legacy info from Phase 1
                    foreach (var user in ovrsUsers)
                    {
                        user.AccountStatus = 0;
                        string fullName = "Unknown";
                        string email = "Unknown";
                        // If user has LegacyUserId, try to get legacy info
                        if (user.LegacyUserId.HasValue)
                        {
                            // Assume _legacyContext is available for Phase 1 DB context
                            var legacyUser = await _legacyContext.TblUsers
                                .FirstOrDefaultAsync(lu => lu.UserId == user.LegacyUserId.Value);
                            if (legacyUser != null)
                            {
                                fullName = $"{legacyUser.FirstName ?? ""} {legacyUser.LastName ?? ""}".Trim();
                                email = legacyUser.Email ?? "Unknown";
                            }
                        }
                        deletedUserSummaries.Add($"OVRS_User: {fullName} ({email})");
                    }
                    totalAffected += ovrsUsers.Count;
                }

                // Handle all Administrator types
                var adminIds = selectedUsers.Where(x =>
                    x.Type == "Administrator" ||
                    x.Type == "IT_Admin" ||
                    x.Type == "Tender_Administrator" ||
                    x.Type == "Vendor_Administrator").Select(x => x.Id).ToList();
                if (adminIds.Any())
                {
                    var admins = await _context.Administrators
                        .Where(a => adminIds.Contains(a.Id))
                        .ToListAsync();
                    foreach (var admin in admins)
                    {
                        admin.AccountStatus = 0;
                        deletedUserSummaries.Add($"{admin.Role}: {admin.FirstName} {admin.LastName} ({admin.Email})");
                    }
                    totalAffected += admins.Count;
                }

                if (totalAffected > 0)
                    await _context.SaveChangesAsync();

                if (totalAffected == 0)
                    return Json(new { success = false, message = "No matching users found." });

                // --- AUDIT LOG: Log bulk user deletion with admin info, time/date, and details ---
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                var saTime = _saTimeService.GetCurrentSouthAfricanTime();
                var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                string changeDescription = $"Bulk user deletion performed by" +
                    $"The following users were deleted from the system:\n- {string.Join("\n- ", deletedUserSummaries)}";

                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "BulkDeleteUsers",
                    changeDescription
                );

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
                string deletedUserSummary = null;

                if (model.Type == "OVRS_User")
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == model.Id && u.Role == "OVRS_User");
                    if (user == null)
                        return Json(new { success = false, message = "User not found." });

                    user.AccountStatus = 0;

                    // Get legacy info if available
                    string fullName = "Unknown";
                    string email = "Unknown";
                    if (user.LegacyUserId.HasValue)
                    {
                        var legacyUser = await _legacyContext.TblUsers
                            .FirstOrDefaultAsync(lu => lu.UserId == user.LegacyUserId.Value);
                        if (legacyUser != null)
                        {
                            fullName = $"{legacyUser.FirstName ?? ""} {legacyUser.LastName ?? ""}".Trim();
                            email = legacyUser.Email ?? "Unknown";
                        }
                    }
                    deletedUserSummary = $"OVRS_User: {fullName} ({email})";
                }
                else if (model.Type == "IT_Admin" || model.Type == "Tender_Administrator" || model.Type == "Vendor_Administrator" || model.Type == "Administrator")
                {
                    var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == model.Id);
                    if (admin == null)
                        return Json(new { success = false, message = "User not found." });

                    admin.AccountStatus = 0;
                    deletedUserSummary = $"{admin.Role}: {admin.FirstName} {admin.LastName} ({admin.Email}) ";
                }
                else
                {
                    return Json(new { success = false, message = "Unknown user type." });
                }

                await _context.SaveChangesAsync();

                // --- AUDIT LOG: Log individual user deletion with admin info, time/date, and details ---
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                var saTime = _saTimeService.GetCurrentSouthAfricanTime();
                var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                string changeDescription = $"User deletion executed.\n" +
                    $"The following user was deleted from the system:\n- {deletedUserSummary}";

                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "DeleteUser",
                    changeDescription
                );

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
                string reactivatedUserSummary = null;

                if (model.Type == "OVRS_User")
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == model.Id && u.Role == "OVRS_User");
                    if (user == null)
                        return Json(new { success = false, message = "User not found." });

                    user.AccountStatus = 1;

                    // Get legacy info if available
                    string fullName = "Unknown";
                    string email = "Unknown";
                    if (user.LegacyUserId.HasValue)
                    {
                        var legacyUser = await _legacyContext.TblUsers
                            .FirstOrDefaultAsync(lu => lu.UserId == user.LegacyUserId.Value);
                        if (legacyUser != null)
                        {
                            fullName = $"{legacyUser.FirstName ?? ""} {legacyUser.LastName ?? ""}".Trim();
                            email = legacyUser.Email ?? "Unknown";
                        }
                    }
                    reactivatedUserSummary = $"OVRS_User: {fullName} ({email})";
                }
                else if (model.Type == "IT_Admin" || model.Type == "Tender_Administrator" || model.Type == "Vendor_Administrator" || model.Type == "Administrator")
                {
                    var admin = await _context.Administrators.FirstOrDefaultAsync(a => a.Id == model.Id);
                    if (admin == null)
                        return Json(new { success = false, message = "User not found." });

                    admin.AccountStatus = 1;
                    reactivatedUserSummary = $"{admin.Role}: {admin.FirstName} {admin.LastName} ({admin.Email})";
                }
                else
                {
                    return Json(new { success = false, message = "Unknown user type." });
                }

                await _context.SaveChangesAsync();

                // --- AUDIT LOG: Log individual user reactivation with admin info, time/date, and details ---
                var (adminId, adminEmail, adminFullName, adminRole) = await GetCurrentAdminAsync();
                var saTime = _saTimeService.GetCurrentSouthAfricanTime();
                var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                string changeDescription = $"User reactivation executed.\n" +
                    $"The following user account was reactivated:\n- {reactivatedUserSummary}";

                await _auditLogService.LogAsync(
                    adminId,
                    adminEmail,
                    adminFullName,
                    adminRole,
                    "ReactivateUser",
                    changeDescription
                );

                return Json(new { success = true, message = "User reactivated." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------
        // ADMINISTRATOR VIEW PROFILE SECTION
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

            // === Use SA Time Service ===
            var nowSa = _saTimeService.GetCurrentSouthAfricanTime();
            var nowUtc = _saTimeService.ConvertSaLocalToUtc(nowSa).ToDateTimeUtc();

            // Check if user is currently blocked
            if (admin.OtpBlockedUntil.HasValue && nowUtc < admin.OtpBlockedUntil.Value)
            {
                var timeLeft = admin.OtpBlockedUntil.Value - nowUtc;
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
                nowUtc.Subtract(admin.LastOtpRequestTime.Value).TotalHours >= 1)
            {
                admin.OtpRequestCount = 0;
                admin.OtpBlockedUntil = null;
            }

            // Check basic cooldown (60 seconds between requests)
            if (admin.LastOtpRequestTime.HasValue &&
                nowUtc.Subtract(admin.LastOtpRequestTime.Value).TotalSeconds < 60)
            {
                var timeLeft = 60 - (int)nowUtc.Subtract(admin.LastOtpRequestTime.Value).TotalSeconds;
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
                admin.OtpBlockedUntil = nowUtc.AddMinutes(15);
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
            var expiryUtc = otpService.GetOtpExpiration(); // already UTC

            // Update rate limiting counters
            admin.OtpRequestCount++;
            admin.LastOtpRequestTime = nowUtc;

            // Save OTP + pending email
            admin.OtpCode = otpCode;
            admin.OtpExpiration = expiryUtc;
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