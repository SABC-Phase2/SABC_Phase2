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
        // Configuration for reading application settings.
        private readonly IConfiguration _configuration;
        // WebHostEnvironment for accessing environment-specific paths and settings.
        private readonly IWebHostEnvironment _env;
        private readonly BlobStorageService _blobStorageService;

        private readonly EmailService _emailService;

        /// <summary>
        /// Constructor: Sets up dependencies for database access, configuration, and environment.
        /// </summary>
        public OVRS_UserController(Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration, IWebHostEnvironment env, EmailService emailService)
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

            _blobStorageService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            _emailService = emailService;
        }

        /// <summary>
        /// Main OVRS tender listing with optional status filtering and pagination.
        /// </summary>
        public IActionResult Index(string status, int page = 1, int pageSize = 9)
        {
            // Query tenders and their documents from DB
            var query = _context.Tenders.Include(t => t.Documents).AsQueryable();

            // If a status filter is provided, filter the query
            if (!string.IsNullOrEmpty(status))
            {
                // Adjust this logic if your tender.Status values differ
                // Normalize status for comparison
                string statusFilter = status.Trim().ToLower();
                query = query.Where(t => t.Status.ToLower().Contains(statusFilter));
            }
            // Pagination
            var totalItems = query.Count();
            var tenders = query
                .OrderByDescending(t => t.DatePublished)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // NOTE: Blob storage setup for files can be handled here if needed


            return View(tenders);
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

            // Generate SAS URLs for each document
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            foreach (var doc in tender.Documents)
            {
                // Use the full path, not just FileName!
                // If you have a BlobName property, use it:
                doc.FilePath = blobService.GetBlobSasUri(doc.BlobName);

                // If you don't have a BlobName property, but know the folder (e.g. 0), construct it:
                // doc.FilePath = blobService.GetBlobSasUri("0/" + doc.FileName);
            }

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
                    companyName = supplier.LegalName;
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
                        companyName = supplier.LegalName;
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
            Console.WriteLine("SubmitTenderApplication called.");
            Console.WriteLine($"TenderId: {TenderId}, LegacyUserId: {LegacyUserId}");
            Console.WriteLine($"UploadedFiles: {(UploadedFiles == null ? "null" : UploadedFiles.Count.ToString())}");

            // Find the OVRS_User by LegacyUserId
            var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == LegacyUserId);

            var tender = _context.Tenders.Find(TenderId);

            if (tender == null)
            {
                Console.WriteLine($"Tender with ID {TenderId} not found.");
                ModelState.AddModelError("", "Invalid Tender ID.");
                var vm = BuildTenderApplicationViewModel(TenderId, user?.Id);
                return View("Tender_Application", vm);
            }
            if (user == null)
            {
                Console.WriteLine($"User with LegacyUserId {LegacyUserId} not found.");
                ModelState.AddModelError("", "Invalid Employee ID (not found in Users table).");
                var vm = BuildTenderApplicationViewModel(TenderId, null);
                return View("Tender_Application", vm);
            }

            // Create and persist the tender application.
            var application = new TenderApplications
            {
                TenderId = TenderId,
                OVRS_UserId = user.Id,
                DateApplied = DateTime.UtcNow,
                DraftId = DraftId // <--- Add this line!
            };

            Console.WriteLine("Adding new TenderApplications entity to context...");
            _context.Applied_For_Tenders.Add(application);
            await _context.SaveChangesAsync();
            Console.WriteLine($"Tender application saved with Id: {application.Id}");

            // Handle uploaded files
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                Console.WriteLine($"UploadedFiles.Count: {UploadedFiles.Count}");
                var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
                int fileIndex = 0;
                foreach (var file in UploadedFiles)
                {
                    Console.WriteLine($"Processing file index {fileIndex}, name: {file?.FileName}, length: {file?.Length}");
                    if (file != null && file.Length > 0)
                    {
                        var blobFileName = $"applications/{application.Id}/{Guid.NewGuid()}_{file.FileName}";
                        Console.WriteLine($"Uploading file to blob storage: {blobFileName}");
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);
                        Console.WriteLine($"File uploaded. Blob URL: {blobUrl}");

                        var doc = new ApplicationDocument
                        {
                            FileName = file.FileName,
                            BlobName = blobFileName,
                            FilePath = blobFileName,
                            TenderApplicationId = application.Id
                        };
                        Console.WriteLine($"Adding ApplicationDocument to context for file: {file.FileName}");
                        _context.ApplicationDocuments.Add(doc);
                    }
                    else
                    {
                        Console.WriteLine($"Skipped file index {fileIndex} due to null or length 0.");
                    }
                    fileIndex++;
                }
                await _context.SaveChangesAsync();
                Console.WriteLine("All uploaded files processed and saved to database.");
            }
            else
            {
                Console.WriteLine("No files uploaded with submission.");
            }

            Console.WriteLine("Submission processing complete. Redirecting to OVRS_Documents.");



            // ===== Add this block to delete the draft if used =====
            // Copy draft documents to main application documents, if DraftId is present
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
                            BlobName = draftDoc.FilePath, // If you want the blob name to be the same as in draft
                            FilePath = draftDoc.FilePath,
                            TenderApplicationId = application.Id
                        };
                        _context.ApplicationDocuments.Add(appDoc);
                    }
                    await _context.SaveChangesAsync();
                }

                // Now, remove the draft and its docs as before
                if (draft.Documents != null)
                {
                    _context.TenderApplicationDraftDocuments.RemoveRange(draft.Documents);
                }
                _context.TenderApplicationDrafts.Remove(draft);
                await _context.SaveChangesAsync();
            }

            // 1. Get logged-in user from SABC Phase2 db (already have 'user')
            // 2. Cross-reference to get email from etender-sabc-test db
            var legacyUserId = user.LegacyUserId;
            // Use the correct context and class for tbl_users in etender-sabc-test
            var etenderUser = _legacyContext.TblUsers
                .FirstOrDefault(u => u.UserId == legacyUserId);

            if (etenderUser != null && !string.IsNullOrEmpty(etenderUser.Email))
            {
                var userName = $"{etenderUser.FirstName} {etenderUser.LastName}";
                var tenderNumber = tender?.TenderNumber ?? "";
                var tenderName = tender?.Title ?? "";
                // Get the time submitted from application.DateApplied
                // If for some reason it's null, fallback to DateTime.UtcNow
                var timeSubmitted = application.DateApplied ?? DateTime.UtcNow;

                await _emailService.SendTenderSubmissionConfirmationAsync(
                    etenderUser.Email,
                    userName,
                    tenderNumber,
                    tenderName,
                    timeSubmitted
                );
            }
            else
            {
                // Optionally log warning: user not found in etender DB or no email
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
        public IActionResult OVRS_Submissions_Drafts()
        {
            // Attempt to get Users.Id from claims FIRST (best practice!)
            var userIdStr = User.FindFirst("UserId")?.Value;
            int userId;

            if (!int.TryParse(userIdStr, out userId))
            {
                // If not present, try to get LegacyUserId, then look up Users.Id
                var legacyUserIdStr = User.FindFirst("LegacyUserId")?.Value;
                if (!int.TryParse(legacyUserIdStr, out int legacyUserId))
                    return Unauthorized();

                var user = _context.Users.FirstOrDefault(u => u.LegacyUserId == legacyUserId);
                if (user == null)
                    return Unauthorized();

                userId = user.Id;
            }

            // Query applications for that user, including related Tender
            var applications = _context.Applied_For_Tenders
                .Where(a => a.OVRS_UserId == userId)
                .Include(a => a.Tender)
                .OrderByDescending(a => a.DateApplied)
                .ToList();

            // Map to SubmissionViewModel
            var model = applications.Select(a => new SubmissionViewModel
            {
                ApplicationId = a.Id, // <-- CRUCIAL: set to the PK for edit links!
                TenderNumber = a.Tender?.TenderNumber ?? "",
                DateSubmitted = a.DateApplied?.ToString("dd/MM/yyyy") ?? "",
                TimeSubmitted = a.DateApplied?.ToString("hh:mm tt") ?? "",
                Status = a.Tender?.Status ?? "",
                ClosingDateTime = a.Tender != null
                    ? $"{a.Tender.ClosingDate:dd/MM/yyyy} @ {(a.Tender.ClosingTime.HasValue ? DateTime.Today.Add(a.Tender.ClosingTime.Value).ToString("hh:mm tt") : "")}"
                    : "",
                TenderId = a.Tender?.Id ?? 0
            }).ToList();

            // Only include drafts for this user
            var joinData = _context.TenderApplicationDrafts
                .Where(d => d.OVRS_UserId == userId && d.TenderId != null)
                .GroupJoin(_context.Tenders,
                    d => d.TenderId,
                    t => t.Id,
                    (d, tenders) => new { d, t = tenders.FirstOrDefault() })
                .ToList(); // Materialize first

            var drafts = joinData
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

            ViewBag.DraftTiles = drafts;

            return View(model);
        }
        /// <summary>
        /// Persists or updates a draft tender application, including draft document uploads.
        /// This allows users to save work-in-progress and resume later.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SaveTenderApplicationDraft( Guid? DraftId,int? LegacyUserId, int? TenderId,List<IFormFile> UploadedFiles)
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
            if (DraftId.HasValue)
            {
                draft = _context.TenderApplicationDrafts.Include(d => d.Documents).FirstOrDefault(d => d.DraftId == DraftId.Value);
                if (draft == null)
                {
                    // Defensive: Draft vanished or bad id.
                    return Json(new { success = false, message = "Draft not found" });
                }
                draft.LastModifiedDate = DateTime.UtcNow;
                // Ensure the OVRS_UserId is set (optional: only if you want to allow changing user on update)
                draft.OVRS_UserId = ovrsUserId;
            }
            else
            {
                draft = new TenderApplicationDraft
                {
                    DraftId = Guid.NewGuid(),
                    CreatedDate = DateTime.UtcNow,
                    OVRS_UserId = ovrsUserId,    // <-- Use the resolved Users.Id
                    TenderId = TenderId,
                    Documents = new List<TenderApplicationDraftDocument>() // Always initialize!
                };
                _context.TenderApplicationDrafts.Add(draft);
            }

            // Handle draft file uploads (if any).
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
                foreach (var file in UploadedFiles)
                {
                    if (file != null && file.Length > 0)
                    {
                        var blobFileName = $"draftapplications/{draft.DraftId}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        var doc = new TenderApplicationDraftDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName
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
        // Redirects the user to a time-limited SAS URL for viewing the PDF in Azure Blob Storage
        public async Task<IActionResult> ViewDocument(int id)
        {
            // Look up the document in the database by its primary key (int Id)
            var doc = await _context.TenderApplicationDraftDocuments.FindAsync(id);
            if (doc == null) return NotFound();

            // Generate a time-limited SAS URL for the blob so the PDF can be viewed in the browser
            var sasUrl = _blobStorageService.GetBlobSasUri(doc.FilePath); // 'FilePath' holds the blob name/key
                                                                          // Redirect the user to the SAS URL (opens PDF in new tab)
            return Redirect(sasUrl);
        }

        // POST: /OVRS_User/DeleteDocument/{id}
        // Deletes the document both from Azure Blob Storage and the database
        [HttpPost]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            // Try to find the document in draft documents first
            var draftDoc = await _context.TenderApplicationDraftDocuments.FindAsync(id);
            if (draftDoc != null)
            {
                await _blobStorageService.DeleteFileAsync(draftDoc.FilePath); // Azure blob
                _context.TenderApplicationDraftDocuments.Remove(draftDoc);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }

            // If not found in draft, try to find it in submitted (final) application documents
            var appDoc = await _context.ApplicationDocuments.FindAsync(id);
            if (appDoc != null)
            {
                await _blobStorageService.DeleteFileAsync(appDoc.FilePath); // Azure blob
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
        public async Task<IActionResult> UpdateTenderSubmission(int TenderId, int? DraftId, int? LegacyUserId, [FromForm] IFormFileCollection UploadedFiles)
        {
            // 1. Find the tender application (final, not draft)
            var tenderApplication = await _context.Applied_For_Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.TenderId == TenderId);

            if (tenderApplication == null)
                return Json(new { success = false, message = "Tender application not found." });

            // 2. Save each uploaded PDF file to Azure Blob Storage and DB
            foreach (var formFile in UploadedFiles)
            {
                if (formFile != null && formFile.Length > 0)
                {
                    // Only accept PDFs
                    if (!formFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var blobFileName = $"{Guid.NewGuid()}_{Path.GetFileName(formFile.FileName)}";

                    // Upload to Azure Blob Storage (assuming this returns the blob URL/path)
                    var filePath = await _blobStorageService.UploadFileAsync(formFile.OpenReadStream(), blobFileName);

                    var doc = new SABC_Phase2.Models.OVRS.ApplicationDocument
                    {
                        FileName = formFile.FileName,
                        BlobName = blobFileName,
                        FilePath = filePath,
                        TenderApplicationId = tenderApplication.Id
                    };

                    _context.ApplicationDocuments.Add(doc);
                }
            }

            await _context.SaveChangesAsync();

            // Replace the response line in your UpdateTenderSubmission POST method with:
            return Json(new { success = true, redirectUrl = Url.Action("OVRS_Submissions_Drafts", "OVRS_User") });
        }
    }
}
