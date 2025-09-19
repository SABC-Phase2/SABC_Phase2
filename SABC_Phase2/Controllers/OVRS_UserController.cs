using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using SABC_Phase2.Data;
using SABC_Phase2.Models.OVRS;
using SABC_Phase2.Models.Phase1_LegacyDB;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;
using SABC_Phase2.ViewModels;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;

namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for all OVRS user-facing operations,
    /// including tender application, tender drafts, and document management.
    /// </summary>
    /// 


 
    public class OVRS_UserController : Controller
    {
        // Dependency-injected database context for EF Core operations.
        private readonly Phase2Context _context;
        private readonly LegacyDbContext _legacyContext;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly EmailService _emailService;
        private readonly SouthAfricanTimeService _saTimeService;
        private readonly OtpService _otpService;
        private readonly PhoneOtpEmailService _phoneOtpEmailService;
        private readonly SmsOtpService _smsOtpService;

        /// <summary>
        /// Constructor: Sets up dependencies for database access, configuration, and environment.
        /// </summary>
        public OVRS_UserController(Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration, IWebHostEnvironment env, EmailService emailService, SouthAfricanTimeService saTimeService, OtpService otpService, PhoneOtpEmailService phoneOtpEmailService, SmsOtpService smsOtpService)
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



            _emailService = emailService;
            _saTimeService = saTimeService;
            _otpService = otpService;
            _phoneOtpEmailService = phoneOtpEmailService;
            _smsOtpService = smsOtpService;
        }

        /// <summary>
        /// Main OVRS tender listing with optional status filtering and pagination.
        /// </summary>
        
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

            // No more BlobName/FilePath logic! Use SharePointPath directly in your views.

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            ViewBag.Status = status;
            ViewBag.Type = type;
            ViewBag.Search = search;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return PartialView("OVRS_TendersTablePartial_Index", tenders);

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

        /// <summary>
        /// Shows all tender details, including secure links to all attached documents.
        /// </summary>
        public IActionResult View_Tender_Details(int id)
        {
            var tender = _context.Tenders
                .Include(t => t.Documents)
                .FirstOrDefault(t => t.Id == id);

            if (tender == null)
                return NotFound();

            // Get current user id from claims
            int? userId = null;
            var userIdClaim = User.FindFirst("UserId");
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int uid))
                userId = uid;

            // Check if already applied
            bool alreadyApplied = false;
            if (userId != null)
            {
                alreadyApplied = _context.Applied_For_Tenders
                    .Any(a => a.OVRS_UserId == userId && a.TenderId == id);
            }

            // Use SharePointPath for each document for viewing/downloading
            foreach (var doc in tender.Documents)
            {
                // No more FilePath or BlobName! Use SharePointPath in your views.
                // If you need to fall back or transform the path, do so here.
                // Example: If SharePointPath is not null, nothing to do. Otherwise, handle error or fallback logic.
                // If you only use SharePoint now, this loop can be removed.
            }

            // Pass both tender and alreadyApplied to view
            ViewBag.AlreadyApplied = alreadyApplied;

            return View(tender);
        }

        /// <summary>
        /// Presents the tender application form for a given tender.
        /// Get method that populates applicant details and tender details
        /// </summary>
        public IActionResult Tender_Application(int id, Guid? draftId)
        {
            var tender = _context.Tenders.Include(t => t.Documents).FirstOrDefault(t => t.Id == id);
            if (tender == null)
                return NotFound();

            // Get current user Id from claims
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "UserId");
            int? currentUserId = null;
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out var uid))
                currentUserId = uid;

            if (!currentUserId.HasValue)
                return Unauthorized();

            // Try to fetch legacy user id
            int? legacyUserId = _context.Users.Where(u => u.Id == currentUserId).Select(u => u.LegacyUserId).FirstOrDefault();

            // 1️⃣ Determine the draft to load
            TenderApplicationDraft draft = null;

            if (draftId.HasValue)
            {
                // Open specific draft
                draft = _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefault(d => d.DraftId == draftId.Value && d.OVRS_UserId == currentUserId);
            }

            if (draft == null)
            {
                // Check if a draft already exists for this user + tender
                draft = _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefault(d => d.TenderId == id && d.OVRS_UserId == currentUserId);
            }

            // 2️⃣ Fetch company info from legacy DB
            string companyEmail = "";
            string companyName = "";
            if (legacyUserId != null)
            {
                var supplier = _legacyContext.TblSuppliers.FirstOrDefault(s => s.UserId == legacyUserId.Value);
                if (supplier != null)
                {
                    companyEmail = supplier.Email;
                    companyName = supplier.TradingName;
                }
            }

            // 3️⃣ Prepare view model
            var vm = new TenderApplicationViewModel
            {
                Tender = tender,
                Draft = draft, // This is either the existing draft or null
                LegacyUserId = legacyUserId,
                CompanyEmail = companyEmail,
                CompanyName = companyName
            };

            return View(vm);
        }




        private TenderApplicationViewModel BuildTenderApplicationViewModel(int tenderId, int? ovrsUserId)
        {
            var tender = _context.Tenders.Include(t => t.Documents).FirstOrDefault(t => t.Id == tenderId);

            int? legacyUserId = null;
            string companyEmail = "";
            string companyName = "";

            if (ovrsUserId != null)
            {
                legacyUserId = _context.Users
                    .Where(u => u.Id == ovrsUserId)
                    .Select(u => u.LegacyUserId)
                    .FirstOrDefault();

                if (legacyUserId != null)
                {
                    var supplier = _legacyContext.TblSuppliers.FirstOrDefault(s => s.UserId == legacyUserId);
                    if (supplier != null)
                    {
                        companyEmail = supplier.Email;
                        companyName = supplier.TradingName;
                    }
                }
            }

            return new TenderApplicationViewModel
            {
                Tender = tender,
                Draft = null, // or fetch as needed
                LegacyUserId = legacyUserId,
                CompanyEmail = companyEmail,
                CompanyName = companyName
            };
        }

        /// <summary>
        /// Handles full tender application submission, including file upload and DB persistence.
        /// </summary>

        [HttpPost]
        public async Task<IActionResult> SubmitTenderApplication(int TenderId, int LegacyUserId, List<IFormFile> UploadedFiles, Guid? DraftId)
        {
            try
            {
                // Find the OVRS_User by LegacyUserId
                var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == LegacyUserId);
                var tender = _context.Tenders.Find(TenderId);

                if (tender == null)
                {
                    return Json(new { success = false, message = "Invalid Tender ID." });
                }

                if (user == null)
                {
                    return Json(new { success = false, message = "Invalid Employee ID (not found in Users table)." });
                }

                // Check if user has already submitted this tender
                var existingApplication = _context.Applied_For_Tenders
                    .FirstOrDefault(a => a.OVRS_UserId == user.Id && a.TenderId == TenderId);

                if (existingApplication != null)
                {
                    return Json(new { success = false, message = "You have already submitted an application for this tender." });
                }

                // If no DraftId provided, check if there's an existing draft for this tender and user
                TenderApplicationDraft draft = null;
                if (DraftId.HasValue)
                {
                    draft = _context.TenderApplicationDrafts
                        .Include(d => d.Documents)
                        .FirstOrDefault(d => d.DraftId == DraftId.Value && d.OVRS_UserId == user.Id);
                }
                else
                {
                    // Look for any existing draft for this tender and user
                    draft = _context.TenderApplicationDrafts
                        .Include(d => d.Documents)
                        .FirstOrDefault(d => d.TenderId == TenderId && d.OVRS_UserId == user.Id);
                }

                // Create and persist the tender application.
                var saLocalNow = _saTimeService.GetCurrentSouthAfricanTime();
                var application = new TenderApplications
                {
                    TenderId = TenderId,
                    OVRS_UserId = user.Id,
                    DateApplied = saLocalNow.ToDateTimeUnspecified(),
                    DraftId = draft?.DraftId // Use the draft ID if we found one
                };

                _context.Applied_For_Tenders.Add(application);
                await _context.SaveChangesAsync();

                // Get company name for SharePoint folder structure
                var supplier = _legacyContext.TblSuppliers.FirstOrDefault(s => s.UserId == LegacyUserId);
                var companyName = supplier?.TradingName ?? $"User_{LegacyUserId}";

                // Sanitize all SharePoint folder/file names!
                var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tender.TenderNumber);
                var safeCompanyName = SanitizeHelper.ToSharePointSafeFolderName(companyName);

                // Use SharePointService for file uploads
                var sharePointService = new SharePointService(_configuration);

                // Handle uploaded files - these go directly to "Application Documents" folder
                if (UploadedFiles != null && UploadedFiles.Any())
                {
                    foreach (var file in UploadedFiles)
                    {
                        if (file != null && file.Length > 0)
                        {
                            using var stream = file.OpenReadStream();
                            var safeFileName = SanitizeHelper.ToSharePointSafeFileName(file.FileName);

                            // This method already creates "Application Documents" folder structure
                            var sharePointUrl = await sharePointService
                                .UploadUserApplicationDocumentAsync(safeTenderNumber, safeCompanyName, stream, file.FileName);

                            var doc = new ApplicationDocument
                            {
                                FileName = file.FileName,
                                SharePointPath = sharePointUrl,
                                TenderApplicationId = application.Id
                            };

                            _context.ApplicationDocuments.Add(doc);
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                // Handle draft (with or without documents)
                if (draft != null)
                {
                    try
                    {
                        // Handle draft documents if they exist - MOVE them from "Draft Docs" to "Application Documents"
                        if (draft.Documents != null && draft.Documents.Any())
                        {
                            // Create a list to hold documents to remove (to avoid modifying collection while iterating)
                            var documentsToRemove = new List<TenderApplicationDraftDocument>();

                            foreach (var draftDoc in draft.Documents.ToList()) // ToList() creates a copy
                            {
                                try
                                {
                                    // Move document from "Draft Docs" to "Application Documents" folder
                                    var newSharePointUrl = await sharePointService
                                        .MoveDraftDocumentToApplicationAsync(draftDoc.SharePointPath, tender.TenderNumber, companyName);

                                    var appDoc = new ApplicationDocument
                                    {
                                        FileName = draftDoc.FileName,
                                        SharePointPath = newSharePointUrl,
                                        TenderApplicationId = application.Id
                                    };
                                    _context.ApplicationDocuments.Add(appDoc);
                                    documentsToRemove.Add(draftDoc);
                                }
                                catch (Exception docEx)
                                {
                                    // If moving fails, fall back to using the original path
                                    Console.WriteLine($"Error moving draft document to application folder: {docEx.Message}");

                                    var appDoc = new ApplicationDocument
                                    {
                                        FileName = draftDoc.FileName,
                                        SharePointPath = draftDoc.SharePointPath,
                                        TenderApplicationId = application.Id
                                    };
                                    _context.ApplicationDocuments.Add(appDoc);
                                    documentsToRemove.Add(draftDoc);
                                }
                            }

                            // Save the new application documents
                            await _context.SaveChangesAsync();

                            // Remove draft documents from database
                            if (documentsToRemove.Any())
                            {
                                _context.TenderApplicationDraftDocuments.RemoveRange(documentsToRemove);
                            }

                            // Clean up empty "Draft Docs" folder after moving all documents
                            try
                            {
                                await sharePointService.DeleteDraftDocsFolderIfEmptyAsync(tender.TenderNumber, companyName);
                            }
                            catch (Exception cleanupEx)
                            {
                                // Log error but don't fail the submission
                                Console.WriteLine($"Error cleaning up Draft Docs folder: {cleanupEx.Message}");
                            }
                        }

                        // Remove the draft from database
                        _context.TenderApplicationDrafts.Remove(draft);
                        await _context.SaveChangesAsync();
                    }
                    catch (Exception draftEx)
                    {
                        // Log the specific draft handling error
                        Console.WriteLine($"Error handling draft during submission: {draftEx.Message}");
                        Console.WriteLine($"Draft handling stack trace: {draftEx.StackTrace}");

                        // Even if draft handling fails, continue with email sending
                        // The application was already created successfully
                    }
                }

                // Send email confirmation
                try
                {
                    var legacyUserId = user.LegacyUserId;
                    var etenderUser = _legacyContext.TblUsers.FirstOrDefault(u => u.UserId == legacyUserId);

                    if (etenderUser != null && !string.IsNullOrEmpty(etenderUser.Email))
                    {
                        var userName = $"{etenderUser.FirstName} {etenderUser.LastName}";
                        var tenderNumber = tender?.TenderNumber ?? "";
                        var tenderName = tender?.Title ?? "";
                        var timeSubmitted = application.DateApplied ?? DateTime.UtcNow;

                        await _emailService.SendTenderSubmissionConfirmationAsync(
                            etenderUser.Email,
                            userName,
                            tenderNumber,
                            tenderName,
                            timeSubmitted
                        );
                    }
                }
                catch (Exception emailEx)
                {
                    // Log email error but don't fail the submission
                    Console.WriteLine($"Error sending confirmation email: {emailEx.Message}");
                }

                // Return success response
                return Json(new
                {
                    success = true,
                    message = "Application submitted successfully!",
                    tenderNumber = tender.TenderNumber,
                    redirectUrl = Url.Action("OVRS_Documents", "OVRS_User")
                });
            }
            catch (Exception ex)
            {
                // Log the detailed exception information
                Console.WriteLine($"Error submitting tender application: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }

                return Json(new { success = false, message = $"An error occurred while submitting your application: {ex.Message}" });
            }
        }
        /// <summary>
        /// Shows documents to OVRS users (could be their own docs, or company-wide).
        /// </summary>
        public IActionResult OVRS_Documents()
        {
            return View();
        }


        /// <summary>
        /// Lists all user submissions (applications) in a view-friendly format.
        /// NOTE: In development, uses fake claims to simulate an authenticated user.
        /// </summary>
        public IActionResult OVRS_Submissions_Drafts(int page = 1, int pageSize = 5, int draftPage = 1, int draftPageSize = 5)
        {
            // Get userId as before...
            var userIdStr = User.FindFirst("UserId")?.Value;
            int userId;
            if (!int.TryParse(userIdStr, out userId))
            {
                var legacyUserIdStr = User.FindFirst("LegacyUserId")?.Value;
                if (!int.TryParse(legacyUserIdStr, out int legacyUserId))
                    return Unauthorized();
                var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == legacyUserId);
                if (user == null)
                    return Unauthorized();
                userId = user.Id;
            }

            // Submissions (paged)
            var allApplications = _context.Applied_For_Tenders
                .Where(a => a.OVRS_UserId == userId)
                .Include(a => a.Tender)
                .OrderByDescending(a => a.DateApplied);

            int totalItems = allApplications.Count();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            var pageApplications = allApplications
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var model = pageApplications.Select(a => new SubmissionViewModel
            {
                ApplicationId = a.Id,
                TenderNumber = a.Tender?.TenderNumber ?? "",
                DateSubmitted = a.DateApplied?.ToString("dd/MM/yyyy") ?? "",
                TimeSubmitted = a.DateApplied?.ToString("HH:mm tt") ?? "",
                Status = a.Tender?.Status ?? "",
                ClosingDateTime = a.Tender != null
                    ? $"{a.Tender.ClosingDate:dd/MM/yyyy} @ {(a.Tender.ClosingTime.HasValue ? DateTime.Today.Add(a.Tender.ClosingTime.Value).ToString("HH:mm tt") : "")}"
                    : "",
                TenderId = a.Tender?.Id ?? 0
            }).ToList();

            // Drafts (paginated for List View)
            var joinData = _context.TenderApplicationDrafts
                .Where(d => d.OVRS_UserId == userId && d.TenderId != null)
                .GroupJoin(_context.Tenders,
                    d => d.TenderId,
                    t => t.Id,
                    (d, tenders) => new { d, t = tenders.FirstOrDefault() })
                .ToList();

            var allDrafts = joinData
                .Select(x => new DraftTileViewModel
                {
                    DraftId = x.d.DraftId,
                    TenderId = x.t != null ? x.t.Id : 0,
                    TenderNumber = x.t != null ? x.t.TenderNumber : "(No Tender)",
                    TenderTitle = x.t != null ? x.t.Title : "(No Tender)",
                    ClosingDate = x.t != null ? x.t.ClosingDate : null,
                    ClosingTime = x.t != null ? x.t.ClosingTime : null,
                    Status = x.t != null ? x.t.Status : null
                })
                .OrderByDescending(x => x.ClosingDate)
                .ToList();

            int draftTotalItems = allDrafts.Count;
            int draftTotalPages = (int)Math.Ceiling(draftTotalItems / (double)draftPageSize);
            var pagedDrafts = allDrafts
                .Skip((draftPage - 1) * draftPageSize)
                .Take(draftPageSize)
                .ToList();

            // For grid view: allDrafts, for list view: pagedDrafts + paging info
            ViewBag.DraftTiles = allDrafts;            // For grid view (all)
            ViewBag.DraftListPaged = pagedDrafts;      // For list view (paged)
            ViewBag.DraftCurrentPage = draftPage;
            ViewBag.DraftPageSize = draftPageSize;
            ViewBag.DraftTotalItems = draftTotalItems;
            ViewBag.DraftTotalPages = draftTotalPages;

            // Submissions paging info
            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;

            return View(model);
        }

        /// <summary>
        /// Persists or updates a draft tender application, including draft document uploads.
        /// This allows users to save work-in-progress and resume later.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SaveTenderApplicationDraft(Guid? DraftId, int? LegacyUserId, int? TenderId, List<IFormFile> UploadedFiles, [FromForm] List<int> DocumentsToDelete)
        {
            // 1. Lookup the correct OVRS_UserId (Users.Id) from the Users table using LegacyUserId
            int? ovrsUserId = null;
            if (LegacyUserId.HasValue)
            {
                var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == LegacyUserId.Value);
                if (user != null)
                    ovrsUserId = user.Id;
            }

            // Defensive: Ensure we have an OVRS_UserId
            if (!ovrsUserId.HasValue)
                return Json(new { success = false, message = "Could not resolve user from LegacyUserId." });

            // 2. Check if a submission already exists for this user and tender
            if (TenderId.HasValue)
            {
                var alreadySubmitted = _context.Applied_For_Tenders
                    .Any(a => a.OVRS_UserId == ovrsUserId && a.TenderId == TenderId.Value);
                if (alreadySubmitted)
                {
                    return Json(new
                    {
                        success = false,
                        alreadySubmitted = true,
                        message = "You cannot create a draft for this tender because you have already created a submission for it."
                    });
                }
            }

            // Draft object to persist.
            TenderApplicationDraft draft;

            // Determine if this is a new draft or an update.
            var saLocalNow = _saTimeService.GetCurrentSouthAfricanTime();

            if (DraftId.HasValue)
            {
                draft = _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefault(d => d.DraftId == DraftId.Value);
                if (draft == null)
                {
                    // Defensive: Draft vanished or bad id.
                    return Json(new { success = false, message = "Draft not found" });
                }
                draft.LastModifiedDate = saLocalNow.ToDateTimeUnspecified();
                draft.OVRS_UserId = ovrsUserId;
            }
            else
            {
                draft = new TenderApplicationDraft
                {
                    DraftId = Guid.NewGuid(),
                    CreatedDate = saLocalNow.ToDateTimeUnspecified(),
                    OVRS_UserId = ovrsUserId,
                    TenderId = TenderId,
                    Documents = new List<TenderApplicationDraftDocument>()
                };
                _context.TenderApplicationDrafts.Add(draft);
            }

            // Get company name for folder structure
            var supplier = _legacyContext.TblSuppliers.FirstOrDefault(s => s.UserId == LegacyUserId);
            var companyName = supplier?.TradingName ?? $"User_{LegacyUserId}";
            var tender = TenderId.HasValue ? _context.Tenders.FirstOrDefault(t => t.Id == TenderId.Value) : null;
            var tenderNumber = tender?.TenderNumber ?? $"Tender_{TenderId}";

            // === Handle Deletion of Documents ===
            if (DocumentsToDelete != null && DocumentsToDelete.Any() && draft.Documents != null)
            {
                var sharePointService = new SharePointService(_configuration);
                var docsToRemove = draft.Documents.Where(d => DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    try
                    {
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    }
                    catch (Exception ex)
                    {
                        // Optionally log error, but proceed to remove from DB
                        Console.WriteLine($"Error deleting draft document from SharePoint: {ex.Message}");
                    }
                    _context.TenderApplicationDraftDocuments.Remove(doc);
                }
            }

            // Handle draft file uploads (if any), using SharePoint
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                var sharePointService = new SharePointService(_configuration);
                foreach (var file in UploadedFiles)
                {
                    if (file != null && file.Length > 0)
                    {
                        // ✅ Skip if file with same name already exists in draft
                        if (draft.Documents.Any(d => d.FileName == file.FileName))
                            continue;

                        using var stream = file.OpenReadStream();

                        // 🔄 CHANGED: Use new method for draft documents - uploads to "Draft Docs" folder
                        var sharePointUrl = await sharePointService
                            .UploadDraftApplicationDocumentAsync(tenderNumber, companyName, stream, file.FileName);

                        var doc = new TenderApplicationDraftDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl,
                            TempGuid = Guid.NewGuid()
                        };
                        draft.Documents.Add(doc);
                    }
                }
            }

            // Commit all changes to the database; handle exceptions for enterprise robustness.
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Return a clear error message for front-end or logs.
                return Json(new { success = false, message = "Error saving draft: " + ex.Message });
            }

            // Return result to front-end (AJAX) for UX feedback.
            return Json(new
            {
                success = true,
                draftId = draft.DraftId,
                tenderNumber = tender?.TenderNumber,
                message = "Draft saved",
                redirectUrl = Url.Action("OVRS_Documents")
            });
        }




        // Redirects the user to the SharePoint document URL for viewing the PDF in SharePoint
        public async Task<IActionResult> ViewDocument(int id)
        {
            // Find the document by id
            var doc = await _context.ApplicationDocuments.FindAsync(id);
            if (doc == null || string.IsNullOrEmpty(doc.SharePointPath))
                return NotFound();

            // If you store the file in SharePoint, redirect to its URL
            return Redirect(doc.SharePointPath);

        }

        // POST: /OVRS_User/DeleteDocument/{id}
        // Deletes the document from SharePoint and the database
        [HttpPost]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            // Try to find the document in draft documents first
            var draftDoc = await _context.TenderApplicationDraftDocuments.FindAsync(id);
            if (draftDoc != null)
            {
                var sharePointService = new SharePointService(_configuration);
                await sharePointService.DeleteDocumentAsync(draftDoc.SharePointPath); // SharePoint
                _context.TenderApplicationDraftDocuments.Remove(draftDoc);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }

            // If not found in draft, try to find it in submitted (final) application documents
            var appDoc = await _context.ApplicationDocuments.FindAsync(id);
            if (appDoc != null)
            {
                var sharePointService = new SharePointService(_configuration);
                await sharePointService.DeleteDocumentAsync(appDoc.SharePointPath); // SharePoint
                _context.ApplicationDocuments.Remove(appDoc);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }

            // Not found in either table
            return Json(new { success = false, message = "Document not found" });
        }


        // OVRS_UserController.cs
        [HttpGet]
        public async Task<IActionResult> Edit_OVRS_Submissions(int id)
        {
            // Fetch the tender application, including related tender and documents
            var tenderApplication = await _context.Applied_For_Tenders
                .Include(t => t.Tender)
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tenderApplication == null)
            {
                return NotFound();
            }

            // Get the OVRS user
            var ovrsUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == tenderApplication.OVRS_UserId);

            // Get the supplier from the legacy DB using LegacyUserId
            SABC_Phase2.Models.Phase1_LegacyDB.TblSuppliers supplier = null;
            int? legacyUserId = null;
            if (ovrsUser != null)
            {
                legacyUserId = ovrsUser.LegacyUserId;
                supplier = await _legacyContext.TblSuppliers
                    .FirstOrDefaultAsync(s => s.UserId == legacyUserId);
            }

            // Optionally, load draft for this application if you want to support draft editing
            TenderApplicationDraft draft = null;
            if (tenderApplication.DraftId.HasValue)
            {
                draft = await _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == tenderApplication.DraftId);
            }

            // Populate the view model
            var viewModel = new TenderApplicationViewModel
            {
                Tender = tenderApplication.Tender,
                LegacyUserId = legacyUserId,
                CompanyEmail = supplier?.Email ?? "",
                CompanyName = supplier?.LegalName ?? "",
                Draft = draft
            };

            // Provide documents for main upload section
            if (draft != null && draft.Documents != null && draft.Documents.Any())
            {
                ViewBag.Documents = draft.Documents;
            }
            else
            {
                ViewBag.Documents = tenderApplication.Documents;
            }

            return View(viewModel);
        }


        // Update the UpdateTenderSubmission method
        [HttpPost]
        public async Task<IActionResult> UpdateTenderSubmission(int TenderId, int? DraftId, int? LegacyUserId, [FromForm] IFormFileCollection UploadedFiles, [FromForm] string DocumentsToDelete)
        {
            var tenderApplication = await _context.Applied_For_Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.TenderId == TenderId);

            if (tenderApplication == null)
                return Json(new { success = false, message = "Tender application not found." });

            var supplier = LegacyUserId.HasValue
                ? _legacyContext.TblSuppliers.FirstOrDefault(s => s.UserId == LegacyUserId.Value)
                : null;
            var companyName = supplier?.TradingName ?? $"User_{LegacyUserId}";
            var tender = await _context.Tenders.FirstOrDefaultAsync(t => t.Id == TenderId);
            var tenderNumber = tender?.TenderNumber ?? $"Tender_{TenderId}";

            var sharePointService = new SharePointService(_configuration);

            // Parse DocumentsToDelete from comma-separated string
            List<int> documentsToDeleteList = new List<int>();
            if (!string.IsNullOrEmpty(DocumentsToDelete))
            {
                var docIdsToDelete = DocumentsToDelete.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var docIdStr in docIdsToDelete)
                {
                    if (int.TryParse(docIdStr.Trim(), out int docId))
                    {
                        documentsToDeleteList.Add(docId);
                    }
                }
            }

            // Deletion logic - now happens during form submission
            if (documentsToDeleteList.Any())
            {
                var docsToRemove = tenderApplication.Documents
                    .Where(d => documentsToDeleteList.Contains(d.Id)).ToList();

                foreach (var doc in docsToRemove)
                {
                    try
                    {
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    }
                    catch (Exception ex)
                    {
                        // Log error if needed but continue with database deletion
                        // You might want to log this: _logger.LogError(ex, "Failed to delete document from SharePoint: {SharePointPath}", doc.SharePointPath);
                    }
                    _context.ApplicationDocuments.Remove(doc);
                }
            }

            // Upload logic for new files
            foreach (var formFile in UploadedFiles)
            {
                if (formFile != null && formFile.Length > 0)
                {
                    if (!formFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        using var stream = formFile.OpenReadStream();
                        var sharePointUrl = await sharePointService
                            .UploadUserApplicationDocumentAsync(tenderNumber, companyName, stream, formFile.FileName);

                        var doc = new SABC_Phase2.Models.OVRS.ApplicationDocument
                        {
                            FileName = formFile.FileName,
                            SharePointPath = sharePointUrl,
                            TenderApplicationId = tenderApplication.Id
                        };

                        _context.ApplicationDocuments.Add(doc);
                    }
                    catch (Exception ex)
                    {
                        // Log error and continue with other uploads
                        // _logger.LogError(ex, "Failed to upload document: {FileName}", formFile.FileName);
                        return Json(new { success = false, message = $"Failed to upload {formFile.FileName}. Please try again." });
                    }
                }
            }

            try
            {
                await _context.SaveChangesAsync();
                return RedirectToAction("OVRS_Submissions_Drafts", "OVRS_User");
            }
            catch (Exception ex)
            {
                // _logger.LogError(ex, "Failed to save changes to database");
                return Json(new { success = false, message = "Failed to save changes. Please try again." });
            }
        }

        public async Task<IActionResult> AllTenders(int page = 1, int pageSize = 7, string search = "", string filter = "all")
        {
            var query = _context.Tenders.AsQueryable();

            // Filter by status
            if (!string.IsNullOrEmpty(filter) && filter != "all")
            {
                string status = filter == "open" ? "open tender"
                               : filter == "closed" ? "closed tender"
                               : "";
                if (!string.IsNullOrEmpty(status))
                    query = query.Where(t => t.Status.ToLower() == status);
            }

            // Search
            if (!string.IsNullOrEmpty(search))
            {
                search = search.ToLower();
                query = query.Where(t =>
                    (t.TenderNumber != null && t.TenderNumber.ToLower().Contains(search)) ||
                    (t.Title != null && t.Title.ToLower().Contains(search)) ||
                    (t.Status != null && t.Status.ToLower().Contains(search)) ||
                    (t.DatePublished != null && t.DatePublished.ToString().ToLower().Contains(search))
                );
            }

            query = query.OrderByDescending(t => t.DatePublished);

            var totalItems = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            var tenders = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalItems = totalItems;
            ViewBag.TotalPages = totalPages;
            ViewBag.Search = search;
            ViewBag.Filter = filter;

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("OVRS_TendersTablePartial_AllTender", tenders);
            }

            return View(tenders);
        }



        public async Task<IActionResult> Awarded_Tender_Details(int id)
        {
            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .Include(t => t.AwardedTender)
                    .ThenInclude(at => at.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tender == null)
                return NotFound();

            string awardedCompanyName = null;
            if (tender.Status?.ToLower() == "awarded tender" && tender.AwardedTenderId.HasValue)
            {
                awardedCompanyName = tender.AwardedTender?.AwardedCompanyName;
            }

            // No more Blob storage logic, use SharePointPath for document URLs in your views
            // If you need to validate or transform SharePointPath, do so here, but normally nothing is needed

            ViewBag.AwardedCompanyName = awardedCompanyName;
            return View(tender);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDraft([FromBody] DeleteDraftRequest request)
        {
            if (request == null || request.DraftId == Guid.Empty)
            {
                return Json(new { success = false, message = "Invalid draft ID." });
            }

            try
            {
                // Fetch the draft + docs
                var draft = await _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == request.DraftId);

                if (draft == null)
                {
                    return Json(new { success = false, message = "Draft not found." });
                }

                // Delete files from SharePoint if needed
                if (draft.Documents != null && draft.Documents.Any())
                {
                    var sharePointService = new SharePointService(_configuration);
                    foreach (var doc in draft.Documents)
                    {
                        try
                        {
                            await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                        }
                        catch (Exception ex)
                        {
                            // Optionally log error, but continue removing DB record
                        }
                    }

                    _context.TenderApplicationDraftDocuments.RemoveRange(draft.Documents);
                }

                _context.TenderApplicationDrafts.Remove(draft);
                await _context.SaveChangesAsync();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting draft: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> OVRS_Profiles()
        {
            // 1. Get the current user's Phase 2 UserId from claims
            var userIdClaim = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
            {
                return Unauthorized();
            }

            // 2. Get the LegacyUserId from Phase 2 DB
            var phase2User = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
            if (phase2User == null || phase2User.LegacyUserId == null)
            {
                return Unauthorized();
            }
            int legacyUserId = phase2User.LegacyUserId.Value;

            // 3. Pull first and last name from Phase 1 (legacy) DB
            var legacyUser = await _legacyContext.TblUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserId == legacyUserId);

            // 4. Pull trading name from suppliers table
            var supplier = await _legacyContext.TblSuppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == legacyUserId);

            // 5. Get country codes from API
            var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
            var countryCodes = await countryCodeService.GetCountryCodesAsync();

            // 6. Prepare ViewModel
            var model = new OVRS_UserProfileViewModel();

            if (legacyUser != null)
            {
                model.FirstName = legacyUser.FirstName ?? "";
                model.LastName = legacyUser.LastName ?? "";

                string countryCode = "+27";
                string phoneNumber = legacyUser.Phone ?? "";

                if (!string.IsNullOrWhiteSpace(phoneNumber))
                {
                    var trimmed = phoneNumber.Trim();
                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\+\d{1,4})[\s\-]?(.+)$");
                    if (match.Success)
                    {
                        countryCode = match.Groups[1].Value;
                        phoneNumber = match.Groups[2].Value.Trim();
                    }
                    else
                    {
                        phoneNumber = trimmed;
                    }
                }

                model.CountryCode = countryCode;
                model.PhoneNumber = phoneNumber;
                model.Email = legacyUser.Email ?? "";
            }
            if (supplier != null)
            {
                model.CompanyName = supplier.TradingName ?? supplier.LegalName ?? "";
            }

            // **Assign the country codes to the model**
            model.CountryCodes = countryCodes;

            return View(model);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OVRS_Profiles(OVRS_UserProfileViewModel model)
        {
            // 1. Validate Model
            if (!ModelState.IsValid)
            {
                // Repopulate country codes for redisplay
                var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                return View(model);
            }

            // 2. Get current user
            var userIdClaim = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
            {
                return Unauthorized();
            }
            var phase2User = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
            if (phase2User == null || phase2User.LegacyUserId == null)
            {
                return Unauthorized();
            }
            int legacyUserId = phase2User.LegacyUserId.Value;

            // 3. Find legacy user
            var legacyUser = await _legacyContext.TblUsers.FirstOrDefaultAsync(u => u.UserId == legacyUserId);
            if (legacyUser == null)
            {
                ModelState.AddModelError("", "Legacy user not found.");
                var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                return View(model);
            }

            // ---------- PASSWORD CHANGE LOGIC ----------
            // Only process if any password fields are filled
            if (!string.IsNullOrWhiteSpace(model.CurrentPassword) ||
                !string.IsNullOrWhiteSpace(model.NewPassword) ||
                !string.IsNullOrWhiteSpace(model.ConfirmPassword))
            {
                // 1. All fields must be filled
                if (string.IsNullOrWhiteSpace(model.CurrentPassword) ||
                    string.IsNullOrWhiteSpace(model.NewPassword) ||
                    string.IsNullOrWhiteSpace(model.ConfirmPassword))
                {
                    ModelState.AddModelError("", "All password fields are required.");
                    var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                    ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                    return View(model);
                }

                // 2. Check new/confirm match
                if (model.NewPassword != model.ConfirmPassword)
                {
                    ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");
                    var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                    ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                    return View(model);
                }

                // 3. Check current password matches db (hashed)
                string currentPasswordHash = PasswordHelper.EncryptPassword(model.CurrentPassword);
                if (legacyUser.Password != currentPasswordHash)
                {
                    ModelState.AddModelError("CurrentPassword", "Current password is incorrect.");
                    var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                    ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                    return View(model);
                }

                // 4. Hash and save new password
                legacyUser.Password = PasswordHelper.EncryptPassword(model.NewPassword);
                legacyUser.UpdatedDate = DateTime.Now;
            }

            // 4. Update user properties (directly, no OTP logic)
            legacyUser.FirstName = model.FirstName;
            
            legacyUser.LastName = model.LastName;
            legacyUser.Email = model.Email;

            // Concatenate country code and phone number for storage (if that's how you store it)
            legacyUser.Phone = $"{model.CountryCode} {model.PhoneNumber}".Trim();

            // Update company name if you store it in TblUsers (if not, update in suppliers table below)

            // 5. Update supplier (company) info if applicable
            var supplier = await _legacyContext.TblSuppliers.FirstOrDefaultAsync(s => s.UserId == legacyUserId);
            if (supplier != null)
            {
                supplier.TradingName = model.CompanyName;
                supplier.LegalName = model.CompanyName; // If you want to update both, or adjust as needed
                                                        // supplier.DisplayAsCompany = model.DisplayAsCompany; // Uncomment if this field exists
            }

            // 6. Save changes
            await _legacyContext.SaveChangesAsync();

            // 7. Success message
            TempData["ProfileUpdateSuccess"] = "Profile updated successfully.";

            // 8. Redirect to GET (Post-Redirect-Get pattern)
            return RedirectToAction(nameof(OVRS_Profiles));
        }


        [HttpPost]
        public async Task<IActionResult> SendEmailOtp([FromBody] string newEmail)
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

            // Get logged in Phase2 user
            var userIdClaim = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
                return Unauthorized();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
            if (user == null) return Unauthorized();

            // Get their legacy user (to check current email)
            var legacyUser = await _legacyContext.TblUsers.FirstOrDefaultAsync(u => u.UserId == user.LegacyUserId);
            if (legacyUser == null)
                return BadRequest(new { success = false, message = "User not found." });

            // --- NEW: Block if changing to own current email ---
            if (legacyUser.Email != null && legacyUser.Email.Trim().ToLower() == newEmail.Trim().ToLower())
            {
                return BadRequest(new { success = false, message = "You are already using this email address." });
            }

            // Check if email is already in use by another account
            var existingUser = await _legacyContext.TblUsers
                .FirstOrDefaultAsync(u => u.Email.ToLower() == newEmail.ToLower() && u.UserId != user.LegacyUserId);

            if (existingUser != null)
                return BadRequest(new { success = false, message = "This email address is already in use by another account." });

            // Generate OTP
            var otpCode = _otpService.GenerateOtpCode();
            var expiry = _otpService.GetOtpExpiration();

            // Save OTP + pending email
            user.OtpCode = otpCode;
            user.OtpExpiration = expiry;
            user.PendingEmail = newEmail;
            user.OtpType = "email";
            await _context.SaveChangesAsync();

            // Send OTP email with error handling
            var emailResult = await _phoneOtpEmailService.SendEmailOtpAsync(newEmail, user.OriginalEmail ?? "User", otpCode);

            if (!emailResult.Success)
            {
                // Clear the OTP data since email failed
                user.OtpCode = null;
                user.OtpExpiration = null;
                user.PendingEmail = null;
                user.OtpType = null;
                await _context.SaveChangesAsync();

                return BadRequest(new
                {
                    success = false,
                    message = "Failed to send verification email. Please check the email address and try again."
                });
            }

            return Ok(new { success = true, message = "OTP sent successfully" });
        }

        // Step 2: Verify OTP and update email
        [HttpPost]
        public async Task<IActionResult> VerifyEmailOtp([FromBody] VerifyOtpRequest request)
        {
            if (string.IsNullOrEmpty(request?.Otp))
                return BadRequest(new { success = false, message = "OTP is required" });

            var userIdClaim = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
                return Unauthorized();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
            if (user == null) return Unauthorized();

            bool valid = _otpService.ValidateOtp(user.OtpCode, user.OtpExpiration, request.Otp);
            if (!valid)
                return BadRequest(new { success = false, message = "Invalid or expired OTP" });

            // ✅ Update both DBs
            if (!string.IsNullOrEmpty(user.PendingEmail))
            {
                var legacyUser = await _legacyContext.TblUsers.FirstOrDefaultAsync(u => u.UserId == user.LegacyUserId);
                if (legacyUser != null)
                {
                    legacyUser.Email = user.PendingEmail;
                    await _legacyContext.SaveChangesAsync();
                }

                user.OriginalEmail = user.PendingEmail;
                user.PendingEmail = null;
                user.OtpCode = null;
                user.OtpExpiration = null;
                user.OtpType = null;

                await _context.SaveChangesAsync();
            }

            return Ok(new { success = true, message = "Email updated successfully" });
        }
        public class VerifyOtpRequest
        {
            public string Otp { get; set; }
        }


   
        //[HttpPost]
        //[ValidateAntiForgeryToken]
        //public async Task<IActionResult> CancelOtp()
        //{
        //    // Get current user
        //    var userIdClaim = User.FindFirst("UserId")?.Value;
        //    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
        //    {
        //        return Json(new { success = false, message = "User not found." });
        //    }

        //    var phase2User = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
        //    if (phase2User == null || phase2User.LegacyUserId == null)
        //    {
        //        return Json(new { success = false, message = "User not found." });
        //    }

        //    // Store the original values to return them to the client
        //    var originalData = new
        //    {
        //        originalEmail = phase2User.OriginalEmail,
        //        originalPhone = phase2User.OriginalPhoneNumber,
        //        originalCountryCode = phase2User.OriginalCountryCode,
        //        otpType = phase2User.OtpType
        //    };

        //    // Clear all OTP-related, pending, and original data
        //    phase2User.OtpCode = null;
        //    phase2User.OtpExpiration = null;
        //    phase2User.PendingPhoneNumber = null;
        //    phase2User.PendingCountryCode = null;
        //    phase2User.PendingEmail = null;
        //    phase2User.OriginalPhoneNumber = null;
        //    phase2User.OriginalCountryCode = null;
        //    phase2User.OriginalEmail = null;
        //    phase2User.OtpType = null;

        //    await _context.SaveChangesAsync();

        //    return Json(new
        //    {
        //        success = true,
        //        message = "OTP verification cancelled successfully.",
        //        originalData = originalData
        //    });
        //}

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAccount(string ConfirmEmail, string ConfirmPhrase)
        {
            try
            {
                // 1. Get the current user's Phase 2 UserId from claims
                var userIdClaim = User.FindFirst("UserId")?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
                {
                    TempData["ErrorMessage"] = "Unable to identify current user. Please log in again.";
                    return RedirectToAction(nameof(OVRS_Profiles));
                }

                // 2. Get the current user's data from Phase 2 DB to validate email
                var phase2User = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
                if (phase2User == null || phase2User.LegacyUserId == null)
                {
                    TempData["ErrorMessage"] = "User account not found. Please log in again.";
                    return RedirectToAction(nameof(OVRS_Profiles));
                }

                // 3. Get the legacy user to validate email
                var legacyUser = await _legacyContext.TblUsers
                    .FirstOrDefaultAsync(u => u.UserId == phase2User.LegacyUserId.Value);

                if (legacyUser == null)
                {
                    TempData["ErrorMessage"] = "User account not found in legacy system.";
                    return RedirectToAction(nameof(OVRS_Profiles));
                }

                // 4. Validate email confirmation
                if (string.IsNullOrWhiteSpace(ConfirmEmail) ||
                    ConfirmEmail.Trim().ToLower() != legacyUser.Email?.ToLower())
                {
                    TempData["ErrorMessage"] = "Email confirmation does not match your current email address.";
                    return RedirectToAction(nameof(OVRS_Profiles));
                }

                // 5. Validate delete phrase
                if (string.IsNullOrWhiteSpace(ConfirmPhrase) ||
                    ConfirmPhrase.Trim() != "Delete my account")
                {
                    TempData["ErrorMessage"] = "Confirmation phrase does not match exactly.";
                    return RedirectToAction(nameof(OVRS_Profiles));
                }

                // 6. Soft delete in Phase 2 by setting AccountStatus = 0
                phase2User.AccountStatus = 0;
                _context.Users.Update(phase2User);
                await _context.SaveChangesAsync();

                // 7. (Optional) If you also want to disable the user in legacy DB instead of deleting:
                // legacyUser.Active = 0;  // Example field if exists
                // await _legacyContext.SaveChangesAsync();

                // 8. Sign out the user
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                TempData["AccountDeletedMessage"] =
                    "Your account has been deactivated successfully. You can no longer log in.";
                return RedirectToAction("AllTenders", "OVRS_User");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in DeleteAccount: {ex.Message}");
                TempData["ErrorMessage"] = "An unexpected error occurred. Please try again or contact support.";
                return RedirectToAction(nameof(OVRS_Profiles));
            }
        }

    }
    public class DeleteDraftRequest
    {
        public Guid DraftId { get; set; }
    }
}