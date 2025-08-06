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
        public TenderAdminController(Phase2Context context, LegacyDbContext legacyContext, IConfiguration configuration, IWebHostEnvironment env, TenderReportPdfService pdfService, SouthAfricanTimeService saTimeService)
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

        [Authorize(Roles = "Administrator")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            if (!User.Identity.IsAuthenticated || !User.IsInRole("Administrator"))
                return Forbid();

            var sharePointService = new SharePointService(_configuration);

            // --- Scheduled Tender Logic ---
            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                try
                {
                    // Combine date and time as South African local time
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

                    // SANITIZE tender number for SharePoint folder
                    var safeTenderFolder = SanitizeHelper.ToSharePointSafeFolderName(scheduledTender.TenderNumber);

                    if (model.UploadedFiles != null && model.UploadedFiles.Any())
                    {
                        foreach (var file in model.UploadedFiles)
                        {
                            if (file.Length > 0)
                            {
                                using var stream = file.OpenReadStream();
                                var sharePointUrl = await sharePointService.UploadDocumentAsync(
                                    safeTenderFolder, // Use sanitized
                                    stream,
                                    file.FileName);

                                scheduledTender.Documents.Add(new ScheduledTenderDocument
                                {
                                    FileName = file.FileName,
                                    SharePointPath = sharePointUrl
                                    // ScheduledTenderId will be filled after SaveChanges
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
                catch (Exception ex)
                {
                    // You can also use a logger here
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

            // SANITIZE tender number for SharePoint folder
            var safeTenderFolder2 = SanitizeHelper.ToSharePointSafeFolderName(tender.TenderNumber);

            // Handle new uploads first
            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                foreach (var file in model.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();
                        var sharePointUrl = await sharePointService.UploadDocumentAsync(
                            safeTenderFolder2, // Use sanitized
                            stream,
                            file.FileName);

                        tender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            SharePointPath = sharePointUrl, // Save SharePoint URL
                            TenderId = tender.Id
                        });
                    }
                }
            }

            // --- Copy PDFs from Draft if present and not already uploaded ---
            if (model.DraftId.HasValue)
            {
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == model.DraftId.Value);

                if (draft != null && draft.Documents != null && draft.Documents.Any())
                {
                    foreach (var draftDoc in draft.Documents)
                    {
                        // Avoid duplicates if user re-uploaded the same file
                        if (!tender.Documents.Any(d => d.FileName == draftDoc.FileName))
                        {
                            tender.Documents.Add(new TenderDocument
                            {
                                FileName = draftDoc.FileName,
                                SharePointPath = draftDoc.SharePointPath,
                                TenderId = tender.Id // will be set after SaveChanges
                            });
                        }
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

            draft.TenderType = form["TenderType"];
            draft.TenderNumber = form["TenderNumber"];
            draft.Status = form["Status"];
            draft.Title = form["Title"];
            draft.Description = form["Description"];

            if (DateTime.TryParse(form["ClosingDate"], out var closingDate))
                draft.ClosingDate = closingDate;
            if (TimeSpan.TryParse(form["ClosingTime"], out var closingTime))
                draft.ClosingTime = closingTime;

            // --- Handle Deletion of Draft Documents ---
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

            // --- Handle Uploads ---
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
                SharePointPath = doc.SharePointPath // <-- use SharePointPath instead of FilePath
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
                    SharePointPath = doc.SharePointPath // <-- use SharePointPath instead of FilePath
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

            var sharePointService = new SharePointService(_configuration);

            // Handle document deletions
            // Handle document deletions
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                if (dto.Status == "Awarded Tender" && tender.AwardedTender != null)
                {
                    var docsToRemove = tender.AwardedTender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                    foreach (var doc in docsToRemove)
                    {
                        // Delete from SharePoint
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                        _context.TenderDocuments.Remove(doc);
                    }
                }
                else
                {
                    var docsToRemove = tender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                    foreach (var doc in docsToRemove)
                    {
                        // Delete from SharePoint
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                        _context.TenderDocuments.Remove(doc);
                    }
                }
            }

            // Handle new PDF uploads
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
                                file.FileName);

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

            // AwardedTender update logic
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
            // Retrieve the scheduled tender from the database, including its associated documents,
            var scheduledTender = await _context.ScheduledTenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (scheduledTender == null)
                return NotFound();

            // Create a DTO and map documents using SharePointPath
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
                    SharePointPath = doc.SharePointPath // <-- Use SharePointPath, not FilePath
                }).ToList() ?? new List<TenderDocumentViewModel>()
            };

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

            // Use your SharePointService for document management
            var sharePointService = new SharePointService(_configuration);

            // Handle deletion of documents:
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                var docsToRemove = scheduledTender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    try
                    {
                        await sharePointService.DeleteDocumentAsync(doc.SharePointPath);
                    }
                    catch (Exception ex)
                    {
                        // Optionally log error here, but continue to remove from DB anyway
                    }
                    _context.ScheduledTendersDocuments.Remove(doc);
                }
            }

            // Handle upload of new documents:
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        using var stream = file.OpenReadStream();
                        var sharePointUrl = await sharePointService.UploadDocumentAsync(
                            scheduledTender.TenderNumber,
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

            return RedirectToAction("ScheduledIndex");
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

