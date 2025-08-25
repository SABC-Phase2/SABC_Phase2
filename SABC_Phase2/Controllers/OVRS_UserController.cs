using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.OVRS;
using SABC_Phase2.Models.Phase1_LegacyDB;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;
using SABC_Phase2.ViewModels;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace SABC_Phase2.Controllers
{
    /// <summary>
    /// Controller responsible for all OVRS user-facing operations,
    /// including tender application, tender drafts, and document management.
    /// </summary>
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


        /// <summary>
        /// Constructor: Sets up dependencies for database access, configuration, and environment.
        /// </summary>
        public OVRS_UserController(Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration, IWebHostEnvironment env, EmailService emailService, SouthAfricanTimeService saTimeService, OtpService otpService)
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
        }

        /// <summary>
        /// Main OVRS tender listing with optional status filtering and pagination.
        /// </summary>
        // Your Index action, updated for partial view & AJAX
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

            TenderApplicationDraft draft = null;
            if (draftId.HasValue)
            {
                draft = _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefault(d => d.DraftId == draftId.Value);
            }

            // Get current user Id from claims
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "UserId");
            int? currentUserId = null;
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out var uid))
                currentUserId = uid;

            int? legacyUserId = null;
            if (currentUserId != null)
            {
                legacyUserId = _context.Users
                    .Where(u => u.Id == currentUserId)
                    .Select(u => u.LegacyUserId)
                    .FirstOrDefault();
            }

            // Fetch company email and name from legacy DB
            string companyEmail = "";
            string companyName = "";
            if (legacyUserId != null)
            {
                var supplier = _legacyContext.TblSuppliers
                    .FirstOrDefault(s => s.UserId == legacyUserId.Value); // <-- use C# property name!
                if (supplier != null)
                {
                    companyEmail = supplier.Email;
                    companyName = supplier.TradingName;
                }
            }

            var vm = new TenderApplicationViewModel
            {
                Tender = tender,
                Draft = draft,
                LegacyUserId = legacyUserId,
                CompanyEmail = companyEmail,
                CompanyName = companyName
            };

            return View(vm); // Pass ViewModel, not tender!
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
            // Find the OVRS_User by LegacyUserId
            var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == LegacyUserId);
            var tender = _context.Tenders.Find(TenderId);

            if (tender == null)
            {
                ModelState.AddModelError("", "Invalid Tender ID.");
                var vm = BuildTenderApplicationViewModel(TenderId, user?.Id);
                return View("Tender_Application", vm);
            }
            if (user == null)
            {
                ModelState.AddModelError("", "Invalid Employee ID (not found in Users table).");
                var vm = BuildTenderApplicationViewModel(TenderId, null);
                return View("Tender_Application", vm);
            }

            // Create and persist the tender application.
            var saLocalNow = _saTimeService.GetCurrentSouthAfricanTime();
            var application = new TenderApplications
            {
                TenderId = TenderId,
                OVRS_UserId = user.Id,
                DateApplied = saLocalNow.ToDateTimeUnspecified(),
                DraftId = DraftId
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

            // Handle uploaded files
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                foreach (var file in UploadedFiles)
                {
                    if (file != null && file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();

                        // Sanitize file name before upload
                        var safeFileName = SanitizeHelper.ToSharePointSafeFolderName(file.FileName);

                        var sharePointUrl = await sharePointService
                            .UploadUserApplicationDocumentAsync(safeTenderNumber, safeCompanyName, stream, safeFileName);

                        var doc = new ApplicationDocument
                        {
                            FileName = file.FileName, // Store original name for user display
                            SharePointPath = sharePointUrl,
                            TenderApplicationId = application.Id
                        };

                        _context.ApplicationDocuments.Add(doc);
                    }
                }
                await _context.SaveChangesAsync();
            }

            // ===== Copy draft documents to main application documents, if DraftId is present =====
            if (DraftId.HasValue)
            {
                var draft = _context.TenderApplicationDrafts
                    .Include(d => d.Documents)
                    .FirstOrDefault(d => d.DraftId == DraftId.Value);

                if (draft != null && draft.Documents != null && draft.Documents.Any())
                {
                    foreach (var draftDoc in draft.Documents)
                    {
                        var appDoc = new ApplicationDocument
                        {
                            FileName = draftDoc.FileName,
                            SharePointPath = draftDoc.SharePointPath,
                            TenderApplicationId = application.Id
                        };
                        _context.ApplicationDocuments.Add(appDoc);
                    }
                    await _context.SaveChangesAsync();

                    // Remove the draft and its docs as before
                    _context.TenderApplicationDraftDocuments.RemoveRange(draft.Documents);
                    _context.TenderApplicationDrafts.Remove(draft);
                    await _context.SaveChangesAsync();
                }
            }

            // 1. Get logged-in user from SABC Phase2 db (already have 'user')
            // 2. Cross-reference to get email from etender-sabc-test db
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
            // After handling the draft deletion (if necessary), redirect the user to the OVRS_Documents page
            return RedirectToAction("OVRS_Documents");
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
                TimeSubmitted = a.DateApplied?.ToString("hh:mm tt") ?? "",
                Status = a.Tender?.Status ?? "",
                ClosingDateTime = a.Tender != null
                    ? $"{a.Tender.ClosingDate:dd/MM/yyyy} @ {(a.Tender.ClosingTime.HasValue ? DateTime.Today.Add(a.Tender.ClosingTime.Value).ToString("hh:mm tt") : "")}"
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
        public async Task<IActionResult> SaveTenderApplicationDraft(
     Guid? DraftId,
     int? LegacyUserId,
     int? TenderId,
     List<IFormFile> UploadedFiles,
     [FromForm] List<int> DocumentsToDelete // <-- Accept this from the form!
 )
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
                        using var stream = file.OpenReadStream();
                        // Save under /TenderNumber/Tender Applications/CompanyName/
                        var sharePointUrl = await sharePointService
                            .UploadUserApplicationDocumentAsync(tenderNumber, companyName, stream, file.FileName);

                        var doc = new TenderApplicationDraftDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl
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
                message = "Draft saved",
                redirectUrl = Url.Action("OVRS_Documents")
            });
        }

        // GET: /OVRS_User/ViewDocument/{id}
        // Redirects the user to the SharePoint document URL for viewing the PDF in SharePoint
        public async Task<IActionResult> ViewDocument(int id)
        {
            // Look up the document in the database by its primary key (int Id)
            var doc = await _context.TenderApplicationDraftDocuments.FindAsync(id);
            if (doc == null) return NotFound();

            // Redirect the user to the SharePoint URL (opens PDF in new tab)
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


        // Add this POST action to your OVRS_UserController
        [HttpPost]
        public async Task<IActionResult> UpdateTenderSubmission(
            int TenderId, int? DraftId, int? LegacyUserId,
            [FromForm] IFormFileCollection UploadedFiles,
            [FromForm] List<int> DocumentsToDelete
        )
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

            // Deletion logic
            if (DocumentsToDelete != null && DocumentsToDelete.Any())
            {
                var docsToRemove = tenderApplication.Documents
                    .Where(d => DocumentsToDelete.Contains(d.Id)).ToList();

                foreach (var doc in docsToRemove)
                {
                    try
                    {
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    }
                    catch
                    {
                        // Log error if needed
                    }
                    _context.ApplicationDocuments.Remove(doc);
                }
            }

            // Upload logic
            foreach (var formFile in UploadedFiles)
            {
                if (formFile != null && formFile.Length > 0)
                {
                    if (!formFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        continue;

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
            }

            await _context.SaveChangesAsync();

            return RedirectToAction("OVRS_Submissions_Drafts", "OVRS_User");
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


        public async Task<IActionResult> OVRS_Profiles()
        {
            // Your existing code...

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

                // Default country code
                string countryCode = "+27";
                string phoneNumber = legacyUser.Phone ?? "";

                // Try to extract actual country code from the phone number if present
                if (!string.IsNullOrWhiteSpace(phoneNumber))
                {
                    // Remove all spaces for easier parsing
                    var trimmed = phoneNumber.Trim();

                    // Find the first space or dash, assuming format "+XXX NNNNNN" or "+XXX-NNNNNN"
                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\+\d{1,4})[\s\-]?(.+)$");
                    if (match.Success)
                    {
                        countryCode = match.Groups[1].Value;
                        phoneNumber = match.Groups[2].Value.Trim();
                    }
                    else
                    {
                        // If no +country code, just use default and whole string as number
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
            ViewBag.CountryCodes = countryCodes;

            // Check if there's a pending OTP verification
            ViewBag.HasPendingOtp = !string.IsNullOrEmpty(phase2User.OtpCode) &&
                                   phase2User.OtpExpiration.HasValue &&
                                   phase2User.OtpExpiration.Value > DateTime.UtcNow;

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OVRS_Profiles(OVRS_UserProfileViewModel model, string action = "")
        {
            // Handle OTP verification
            if (action == "verify-otp")
            {
                return await VerifyOtp(model.OtpCode);
            }

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

            // 4. Check if phone number has changed
            string currentFullPhone = $"{model.CountryCode} {model.PhoneNumber}".Trim();
            bool phoneNumberChanged = legacyUser.Phone != currentFullPhone;

            if (phoneNumberChanged)
            {
                // Generate OTP and store pending phone number
                string otpCode = _otpService.GenerateOtpCode();
                DateTime otpExpiration = _otpService.GetOtpExpiration();

                phase2User.OtpCode = otpCode;
                phase2User.OtpExpiration = otpExpiration;
                phase2User.PendingPhoneNumber = model.PhoneNumber;
                phase2User.PendingCountryCode = model.CountryCode;

                await _context.SaveChangesAsync();

                // TODO: Send OTP via SMS to the new phone number
                // await SendOtpSms(currentFullPhone, otpCode);

                ViewBag.ShowOtpModal = true;
                ViewBag.OtpSentTo = currentFullPhone;
                ViewBag.CountryCodes = await HttpContext.RequestServices.GetRequiredService<CountryCodeService>().GetCountryCodesAsync();

                TempData["InfoMessage"] = $"An OTP has been sent to {currentFullPhone}. Please enter the code to verify your new phone number.";
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

                // 3. Check current password matches db
                // WARNING: This assumes you store passwords in plain text, which is not secure.
                // If you hash passwords, use a proper hash check here.
                if (legacyUser.Password != model.CurrentPassword)
                {
                    ModelState.AddModelError("CurrentPassword", "Current password is incorrect.");
                    var countryCodeService = HttpContext.RequestServices.GetRequiredService<CountryCodeService>();
                    ViewBag.CountryCodes = await countryCodeService.GetCountryCodesAsync();
                    return View(model);
                }

                // 4. Save new password
                legacyUser.Password = model.NewPassword;
                legacyUser.UpdatedDate = DateTime.Now;
            }

            // 5. Update other user properties (non-phone)
            legacyUser.FirstName = model.FirstName;
            legacyUser.LastName = model.LastName;
            legacyUser.Email = model.Email;

            // 6. Save changes
            await _legacyContext.SaveChangesAsync();

            // 7. Success message
            TempData["ProfileUpdateSuccess"] = "Profile updated successfully.";

            // 8. Redirect to GET (Post-Redirect-Get pattern)
            return RedirectToAction(nameof(OVRS_Profiles));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOtp(string otpCode)
        {
            // Get current user
            var userIdClaim = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int phase2UserId))
            {
                return Json(new { success = false, message = "User not found." });
            }

            var phase2User = await _context.Users.FirstOrDefaultAsync(u => u.Id == phase2UserId);
            if (phase2User == null || phase2User.LegacyUserId == null)
            {
                return Json(new { success = false, message = "User not found." });
            }

            // Validate OTP
            bool isValidOtp = _otpService.ValidateOtp(phase2User.OtpCode, phase2User.OtpExpiration, otpCode);

            if (!isValidOtp)
            {
                return Json(new { success = false, message = "Invalid or expired OTP code." });
            }

            // OTP is valid, update the phone number
            var legacyUser = await _legacyContext.TblUsers.FirstOrDefaultAsync(u => u.UserId == phase2User.LegacyUserId.Value);
            if (legacyUser != null)
            {
                string newFullPhone = $"{phase2User.PendingCountryCode} {phase2User.PendingPhoneNumber}".Trim();
                legacyUser.Phone = newFullPhone;
                legacyUser.UpdatedDate = DateTime.Now;

                await _legacyContext.SaveChangesAsync();
            }

            // Clear OTP data
            phase2User.OtpCode = null;
            phase2User.OtpExpiration = null;
            phase2User.PendingPhoneNumber = null;
            phase2User.PendingCountryCode = null;

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Phone number updated successfully!" });
        }
    }
}