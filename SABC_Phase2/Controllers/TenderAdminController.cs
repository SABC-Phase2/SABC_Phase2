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
    
        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        /// <param name="context">Database context for Tender operations.</param>
        /// <param name="configuration">App configuration settings, used for services like Azure Blob Storage.</param>
        public TenderAdminController(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
           
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
            bool isDraft = Request.Form["IsDraft"] == "true";
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // 1. If this is a draft being submitted as a full tender (i.e., all required fields are filled and not saving as draft)
            if (!isDraft && model.Id > 0)
            {
                // Validate required fields
                if (!ModelState.IsValid)
                {
                    // Repopulate existing docs for redisplay
                    var draft = await _context.TenderAdminsDraft
                        .Include(d => d.Documents)
                        .FirstOrDefaultAsync(d => d.Id == model.Id);

                    if (draft != null)
                    {
                        ViewBag.DraftDocuments = draft.Documents.Select(doc => new
                        {
                            doc.Id,
                            doc.FileName,
                            SasUrl = blobService.GetBlobSasUri(doc.FilePath)
                        }).ToList();
                    }
                    return View(model);
                }

                // Get the draft and its documents
                var draftToPublish = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.Id == model.Id);

                if (draftToPublish == null)
                {
                    return NotFound();
                }

                // Create the main Tender
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

                // Move draft documents to TenderDocuments
                foreach (var draftDoc in draftToPublish.Documents)
                {
                    tender.Documents.Add(new TenderDocument
                    {
                        FileName = draftDoc.FileName,
                        FilePath = draftDoc.FilePath
                    });
                }

                // Add any newly uploaded files
                if (model.UploadedFiles != null && model.UploadedFiles.Any())
                {
                    foreach (var file in model.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            var blobFileName = $"{Guid.NewGuid()}_{file.FileName}";
                            using var stream = file.OpenReadStream();
                            var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                            tender.Documents.Add(new TenderDocument
                            {
                                FileName = file.FileName,
                                FilePath = blobFileName
                            });
                        }
                    }
                }

                // Save the new tender
                _context.Tenders.Add(tender);
                await _context.SaveChangesAsync(); // Save tender first to get its Id
                                                   // Enqueue the draft cleanup job
                BackgroundJob.Enqueue<IDraftCleanupService>(x => x.CleanupPublishedDraftAsync(draftToPublish.Id));
                return RedirectToAction("Index");
            }

            // 2. If saving as draft (new or update)
            if (isDraft)
            {
                // If updating an existing draft
                if (model.Id > 0)
                {
                    var existingDraft = await _context.TenderAdminsDraft
                        .Include(d => d.Documents)
                        .FirstOrDefaultAsync(d => d.Id == model.Id);

                    if (existingDraft == null)
                        return NotFound();

                    existingDraft.TenderType = string.IsNullOrWhiteSpace(model.TenderType) ? null : model.TenderType;
                    existingDraft.TenderNumber = string.IsNullOrWhiteSpace(model.TenderNumber) ? null : model.TenderNumber;
                    existingDraft.ClosingDate = model.ClosingDate == default ? null : model.ClosingDate;
                    existingDraft.ClosingTime = model.ClosingTime;
                    existingDraft.Status = string.IsNullOrWhiteSpace(model.Status) ? null : model.Status;
                    existingDraft.Title = string.IsNullOrWhiteSpace(model.Title) ? null : model.Title;
                    existingDraft.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description;
                    existingDraft.LastModifiedDate = DateTime.UtcNow;

                    // Add new uploaded files to draft
                    if (model.UploadedFiles != null && model.UploadedFiles.Any())
                    {
                        foreach (var file in model.UploadedFiles)
                        {
                            if (file.Length > 0)
                            {
                                var blobFileName = $"{Guid.NewGuid()}_{file.FileName}";
                                using var stream = file.OpenReadStream();
                                var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                                existingDraft.Documents.Add(new TenderDraftDocument
                                {
                                    FileName = file.FileName,
                                    FilePath = blobFileName
                                });
                            }
                        }
                    }

                    await _context.SaveChangesAsync();
                    return RedirectToAction("DraftIndex");
                }
                else // New draft
                {
                    var draft = new TenderDraft
                    {
                        CreatedDate = DateTime.UtcNow,
                        LastModifiedDate = DateTime.UtcNow,
                        TenderType = string.IsNullOrWhiteSpace(model.TenderType) ? null : model.TenderType,
                        TenderNumber = string.IsNullOrWhiteSpace(model.TenderNumber) ? null : model.TenderNumber,
                        ClosingDate = model.ClosingDate == default ? null : model.ClosingDate,
                        ClosingTime = model.ClosingTime,
                        Status = string.IsNullOrWhiteSpace(model.Status) ? null : model.Status,
                        Title = string.IsNullOrWhiteSpace(model.Title) ? null : model.Title,
                        Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description,
                        Documents = new List<TenderDraftDocument>()
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

                                draft.Documents.Add(new TenderDraftDocument
                                {
                                    FileName = file.FileName,
                                    FilePath = blobFileName
                                });
                            }
                        }
                    }

                    _context.TenderAdminsDraft.Add(draft);
                    await _context.SaveChangesAsync();

                    return RedirectToAction("DraftIndex");
                }
            }

            // 3. Normal tender creation (not from draft)
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var newTender = new Tender
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

            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                foreach (var file in model.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        var blobFileName = $"{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        newTender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName
                        });
                    }
                }
            }

            _context.Tenders.Add(newTender);
            await _context.SaveChangesAsync();

            return RedirectToAction("Index");
        }


        /// <summary>
        /// Displays a list of all published tenders with signed-access URIs for their documents.
        /// </summary>

        public IActionResult Index()
        {
            var tenders = _context.Tenders
                .Select(t => new Tender
                {
                    Id = t.Id,
                    TenderType = t.TenderType,
                    TenderNumber = t.TenderNumber,
                    ClosingDate = t.ClosingDate,
                    Status = t.Status,
                    Title = t.Title,
                    Description = t.Description,
                    DatePublished = t.DatePublished, // ✅ ADD THIS LINE
                    Documents = t.Documents.ToList()
                })
                .ToList();
            // Generate SAS URIs for document downloads
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            foreach (var tender in tenders)
            {
                foreach (var doc in tender.Documents)
                {
                    // Replace file path with SAS token-secured download URL
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            return View(tenders);
        }


        public IActionResult DraftIndex()
        {
            var drafts = _context.TenderAdminsDraft
                .OrderByDescending(d => d.CreatedDate)
                .ToList();
            return View(drafts);
        }


        [HttpGet]
        public IActionResult ContinueDraft(int id)
        {
            var draft = _context.TenderAdminsDraft
                .Include(d => d.Documents)
                .FirstOrDefault(d => d.Id == id);

            if (draft == null)
            {
                return NotFound();
            }

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // Prepare document view models with SAS URLs
            var draftDocs = draft.Documents?.Select(doc => new
            {
                doc.Id,
                doc.FileName,
                SasUrl = blobService.GetBlobSasUri(doc.FilePath)
            }).ToList();

            var model = new TenderViewModel
            {
                Id = draft.Id,
                TenderType = draft.TenderType,
                TenderNumber = draft.TenderNumber,
                ClosingDate = draft.ClosingDate ?? default,
                ClosingTime = draft.ClosingTime,
                Status = draft.Status,
                Title = draft.Title,
                Description = draft.Description,
                IsDraft = true
            };

            ViewBag.DraftDocuments = draftDocs;

            return View("Create", model);
        }


    }
}


