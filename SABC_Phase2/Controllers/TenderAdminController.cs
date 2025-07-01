using Hangfire;
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
        private readonly Phase2Context _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        private readonly TenderReportPdfService _pdfService;


        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        public TenderAdminController( Phase2Context context, IConfiguration configuration,IWebHostEnvironment env, TenderReportPdfService pdfService)
        {
            // Assign the injected database context to a private field for use throughout the controller.
            // This context enables database operations such as querying and saving tenders.
            _context = context;

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
        /// <param name="model">Tender data entered by the user.</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            // Validate the incoming model. If the model is not valid (e.g., required fields are missing or invalid),
            // re-display the form with the user's input and validation errors.
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Initialize the Azure Blob Storage service for file uploads.
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // --- Scheduled Tender Logic ---
            // If the tender should be published in the future (scheduled), handle differently.
            if (model.IsScheduled && model.ScheduledDate.HasValue && model.ScheduledTime.HasValue)
            {
                // Combine the scheduled date and time (assumed to be in the user's local timezone).
                DateTime localPublishDateTime = model.ScheduledDate.Value.Date + model.ScheduledTime.Value;

                // Convert the combined local datetime to UTC before saving to ensure consistent time handling.
                DateTime publishDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(localPublishDateTime);

                // Create a new ScheduledTender entity and populate its properties from the model.
                var scheduledTender = new ScheduledTender
                {
                    TenderType = model.TenderType,
                    TenderNumber = model.TenderNumber,
                    ClosingDate = model.ClosingDate.Value,
                    ClosingTime = model.ClosingTime,
                    Status = "Scheduled", // Set status to indicate it's a scheduled tender.
                    Title = model.Title,
                    Description = model.Description,
                    ScheduledPublishDateTime = publishDateTimeUtc, // Store as UTC in DB.
                    CreatedOn = DateTime.UtcNow,
                    Documents = new List<ScheduledTenderDocument>()
                };

                // Handle any uploaded files and add them to the scheduled tender.
                if (model.UploadedFiles != null && model.UploadedFiles.Any())
                {
                    foreach (var file in model.UploadedFiles)
                    {
                        if (file.Length > 0)
                        {
                            // Create a unique filename for each uploaded document.
                            var blobFileName = $"{Guid.NewGuid()}_{file.FileName}";
                            using var stream = file.OpenReadStream();
                            // Upload the file to blob storage and get its URL (not persisted here, but can be used if needed).
                            var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                            // Associate the uploaded document with the scheduled tender.
                            scheduledTender.Documents.Add(new ScheduledTenderDocument
                            {
                                FileName = file.FileName,
                                FilePath = blobFileName
                            });
                        }
                    }
                }

                // Add the new scheduled tender to the database context and save changes.
                _context.ScheduledTenders.Add(scheduledTender);
                await _context.SaveChangesAsync();

                // Schedule a background job (using Hangfire) to publish the tender at the scheduled UTC time.
                var delay = publishDateTimeUtc - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero; // Prevent negative delays (publish immediately if in the past).

                BackgroundJob.Schedule<ITenderPublishingService>(
                    service => service.PublishScheduledTendersAsync(), // The service method to execute.
                    delay);                                            // The delay before the job runs.

                // Redirect to the main index after scheduling.
                return Json(new
                {
                    success = true,
                    tenderNumber = scheduledTender.TenderNumber,
                    redirectUrl = Url.Action("Index", "TenderAdmin"),
                    scheduledTendersUrl = Url.Action("ScheduledIndex", "TenderAdmin")
                });
            }

            // --- Immediate Tender Logic ---
            // If not scheduled, create and save the tender immediately.
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
                DraftId = model.DraftId,            // Link to draft if this was created from a draft.
                Documents = new List<TenderDocument>(),
                AwardedTender = null                // Explicitly set to null (optional).
            };

            // Handle uploaded files for immediate tenders.
            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                foreach (var file in model.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        // Use the tender's ID and a GUID to generate a unique blob filename.
                        var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        // Upload the file to blob storage.
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        // Add the document to the tender's list of documents.
                        tender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName,
                            TenderId = tender.Id
                        });
                    }
                }
            }

            // Save the new tender to the database.
            _context.Tenders.Add(tender);
            await _context.SaveChangesAsync();

            // --- Draft Cleanup Logic ---
            // If this tender was created from a draft, delete the draft and its documents.
            if (model.DraftId.HasValue)
            {
                // Retrieve the draft (and its documents) from the database.
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.DraftId == model.DraftId.Value);

                if (draft != null)
                {
                    // Remove all draft documents from the database.
                    if (draft.Documents != null && draft.Documents.Any())
                    {
                        _context.TenderAdminsDraftDocuments.RemoveRange(draft.Documents);
                    }
                    // Remove the draft itself.
                    _context.TenderAdminsDraft.Remove(draft);

                    // Save changes after deleting the draft and documents.
                    await _context.SaveChangesAsync();
                }
            }

            // Redirect to the tender index after successful creation.
            // For regular publish, return JSON for modal popup
            return Json(new
            {
                success = true,
                tenderNumber = tender.TenderNumber,
                redirectUrl = Url.Action("Index", "TenderAdmin")
            });
        }

        public IActionResult Index(int page = 1, int pageSize = 9)
        {
            // Get the total number of tender records in the database.
            // This is used for pagination calculations and for displaying the total count to the user.
            var totalItems = _context.Tenders.Count();

            // Retrieve a paginated list of tenders from the database.
            // - Include related Documents for each tender so their file information is available in the view.
            // - Order the tenders by DatePublished in descending order (most recently published tenders first).
            // - Skip tenders from previous pages to get the correct subset for the current page.
            // - Take only the number of items equal to the page size (to support paging).
            var tenders = _context.Tenders
                .Include(t => t.Documents)
                .OrderByDescending(t => t.DatePublished)
                .Skip((page - 1) * pageSize) // Calculate number of items to skip based on the current page.
                .Take(pageSize)              // Take only the tenders for this page.
                .ToList();

            // Initialize the Azure Blob Storage service to generate secure access URIs for document downloads/views.
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);
            foreach (var tender in tenders)
            {
                // For each document attached to the tender, generate a SAS URI through the blob service.
                // This ensures that file links are secure and time-limited, and that the FilePath property in the document
                // contains a valid URL for the client to access the file from blob storage.
                foreach (var doc in tender.Documents)
                {
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            // Store pagination and count information in the ViewBag for use in the UI:
            ViewBag.CurrentPage = page;                                // The current page number.
            ViewBag.PageSize = pageSize;                               // The number of tenders per page.
            ViewBag.TotalItems = totalItems;                           // The total number of tenders in the database.
            ViewBag.TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize); // The total number of pages for navigation.

            // Return the view, providing the paginated, document-enriched list of tenders for display.
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
                    CreatedDate = DateTime.UtcNow,                   // Set creation date to now (UTC)
                    Documents = new List<TenderDraftDocument>()      // Initialize the documents collection
                };
                _context.TenderAdminsDraft.Add(draft);               // Add the new draft to EF context for saving
            }
            else
            {
                // If updating an existing draft, set the last modified date to now (UTC)
                draft.LastModifiedDate = DateTime.UtcNow;

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
        public IActionResult DraftIndex()
        {
            // Query the TenderAdminsDraft table to retrieve all draft tenders from the database.
            // - Include the related Documents navigation property so that each draft's attached documents are loaded.
            // - Order the drafts by their creation date in descending order (most recently created drafts appear first).
            var drafts = _context.TenderAdminsDraft
                .Include(d => d.Documents)                // Eagerly load associated documents for each draft
                .OrderByDescending(d => d.CreatedDate)    // Sort by newest drafts at the top
                .ToList();                                // Execute the query and materialize the results as a list

            // Pass the list of draft tenders (with their documents) to the corresponding view for display.
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
                ClosingDate = draft.ClosingDate ?? default,   // Date the tender closes; will set below if null
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
            if (draft.ClosingDate == null)
                model.ClosingDate = DateTime.Today;

            // Reuse the "Create" view for editing drafts, passing in the populated model.
            // The view will display the draft's details for editing.
            return View("Create", model);
        }


        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            // Attempt to retrieve the tender with the specified ID from the database.
            // Include the Documents navigation property so we can display/edit attached documents in the view.
            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            // If no tender is found for the given ID, return a 404 Not Found response.
            if (tender == null)
                return NotFound();

            // Map the tender entity to a data transfer object (DTO) for editing.
            // This DTO is used to transfer all necessary field values to the view.
            var dto = new TenderEditDto
            {
                Id = tender.Id,                           // Unique database ID of the tender
                TenderType = tender.TenderType,           // Type/category of the tender
                TenderNumber = tender.TenderNumber,       // Reference number for the tender
                ClosingDate = tender.ClosingDate,         // Date the tender closes
                ClosingTime = tender.ClosingTime,         // Time on closing date when the tender closes
                Status = tender.Status,                   // Current status (e.g., Open, Closed)
                Title = tender.Title,                     // Title of the tender
                Description = tender.Description,         // Description/details of the tender
                AwardedTender = tender.AwardedTender,     // Awarded tender information (if applicable)
                                                          // Map all existing document entities to view models for display in the edit form.
                ExistingDocuments = tender.Documents?.Select(doc => new TenderDocumentViewModel
                {
                    Id = doc.Id,                         // Unique ID of the document
                    FileName = doc.FileName,             // Original file name for display
                    FilePath = doc.FilePath              // Path or URL to the file in storage
                }).ToList() ?? new List<TenderDocumentViewModel>() // If no documents, provide an empty list so the view doesn't break
            };

            // Render the "Edit" view, passing in the populated DTO so the form is pre-filled with the current tender details.
            return View("Edit", dto);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, TenderEditDto dto)
        {
            // Check if the incoming form data is valid according to the model's validation rules.
            // If not valid, re-render the Edit view with the current DTO to display validation errors.
            if (!ModelState.IsValid)
            {
                return View("Edit", dto);
            }

            // Attempt to retrieve the tender from the database, including its associated documents,
            // using the provided tender ID. This allows us to update both the tender and its documents.
            var tender = await _context.Tenders
                .Include(t => t.Documents)
                .FirstOrDefaultAsync(t => t.Id == id);

            // If the tender does not exist (wrong ID or deleted), return a 404 Not Found response.
            if (tender == null)
                return NotFound();

            // Update the tender's properties with the values provided from the DTO (submitted form).
            tender.TenderType = dto.TenderType;
            tender.TenderNumber = dto.TenderNumber;
            tender.ClosingDate = dto.ClosingDate.Value; // Ensure nullable date is set
            tender.ClosingTime = dto.ClosingTime;
            tender.Status = dto.Status;
            tender.Title = dto.Title;
            tender.Description = dto.Description;
            tender.AwardedTender = dto.AwardedTender; // Update awarded tender info if provided

            // Initialize the blob storage service for document upload/deletion operations.
            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            // Handle document deletions:
            // For each document ID marked for deletion, delete the file from blob storage and remove the record from the database.
            if (dto.DocumentsToDelete != null && dto.DocumentsToDelete.Any())
            {
                // Find documents to remove by matching IDs from the DTO.
                var docsToRemove = tender.Documents.Where(d => dto.DocumentsToDelete.Contains(d.Id)).ToList();
                foreach (var doc in docsToRemove)
                {
                    // Delete the file from Azure Blob Storage.
                    await blobService.DeleteFileAsync(doc.FilePath);
                    // Remove the document entity from the database context.
                    _context.TenderDocuments.Remove(doc);
                }
            }

            // Handle new PDF uploads:
            // For each uploaded file, upload it to blob storage and add a new document entry to the tender.
            if (dto.UploadedFiles != null && dto.UploadedFiles.Any())
            {
                foreach (var file in dto.UploadedFiles)
                {
                    if (file.Length > 0)
                    {
                        // Generate a unique file name and upload the file to Azure Blob Storage.
                        var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{file.FileName}";
                        using var stream = file.OpenReadStream();
                        var blobUrl = await blobService.UploadFileAsync(stream, blobFileName);

                        // Create a new TenderDocument entity and associate it with the current tender.
                        tender.Documents.Add(new TenderDocument
                        {
                            FileName = file.FileName,
                            FilePath = blobFileName,
                            TenderId = tender.Id
                        });
                    }
                }
            }

            // Persist all changes (tender updates, document deletions, and new uploads) to the database.
            await _context.SaveChangesAsync();

            // Check for Awarded Tender modal logic
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

            // Default: redirect as usual
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult ScheduledIndex(int page = 1, int pageSize = 9)
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
                // Set the local timezone (change string if your region is different)
                var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
                // Combine the scheduled date and time to get the local scheduled datetime
                var localDateTime = dto.ScheduledDate.Value.Date + dto.ScheduledTime.Value;
                // Convert the local scheduled datetime to UTC for consistent storage and comparison
                scheduledTender.ScheduledPublishDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, userTimeZone);
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
        //ReportsIndex()

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


        [HttpGet]
        public IActionResult GenerateTenderSupplierReport(int id, DateTime? startDate = null, DateTime? endDate = null)
        {
            // Attempt to retrieve the tender with the specified ID from the database.
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);

            // If the tender does not exist, return a 404 Not Found response.
            if (tender == null)
                return NotFound();

            // Prepare a query to fetch all applications for this tender, including related supplier (OVRS_User) information.
            var query = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User) // Eagerly load supplier/user data for each application
                .Where(a => a.TenderId == id); // Filter by the specified tender ID

            // If both start and end dates are provided, filter applications by the specified date range.
            if (startDate.HasValue && endDate.HasValue)
            {
                // Adjust endDate to include the entire day (up to the last tick).
                var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);
                query = query.Where(a => a.DateApplied >= startDate && a.DateApplied <= endDateInclusive);
            }

            // Execute the query and materialize the results as a list.
            var applications = query.ToList();

            // Use the PDF service to generate a supplier report for the tender from the list of applications.
            // This returns the PDF as a byte array.
            var pdfBytes = _pdfService.GenerateSupplierReport(tender, applications);

            // Return the generated PDF file as a downloadable file to the user.
            // - The MIME type "application/pdf" indicates a PDF document.
            // - The filename includes the tender number for clarity.
            return File(pdfBytes, "application/pdf", $"SupplierReport_Tender_{tender.TenderNumber}.pdf");
        }




        [HttpGet]
        public IActionResult GenerateClosedTendersSummaryReport(DateTime? startDate, DateTime? endDate)
        {
            // Validate input: Ensure both startDate and endDate are provided.
            // If not, return a 400 Bad Request response with a descriptive error message.
            if (!startDate.HasValue || !endDate.HasValue)
                return BadRequest("Start and end date required");

            // Adjust endDate to be inclusive by moving to the last tick of the specified day.
            // This ensures tenders closing on endDate are included in the results.
            var endDateInclusive = endDate.Value.AddDays(1).AddTicks(-1);

            // Query: Retrieve all tenders that:
            // - Have a non-null Status containing the word "closed" (case-insensitive)
            // - Have a ClosingDate within the provided date range (inclusive)
            var tenders = _context.Tenders
                .Where(t => t.Status != null && t.Status.ToLower().Contains("closed")
                         && t.ClosingDate >= startDate && t.ClosingDate <= endDateInclusive)
                .ToList();

            // Collect all tender IDs from the filtered tenders for use in the applications query.
            var tenderIds = tenders.Select(t => t.Id).ToList();

            // Query: Fetch all applications for the filtered tenders.
            // - Include the related OVRS_User (supplier) data for each application.
            var allApps = _context.Applied_For_Tenders
                .Include(a => a.OVRS_User)
                .Where(a => tenderIds.Contains(a.TenderId))
                .ToList();

            // Construct a summary row for each tender:
            // - Count local and foreign suppliers, and total applicants per tender.
            // - Each summary row corresponds to a single closed tender.
            var summaryList = tenders.Select(tender =>
            {
                // Applications related to the current tender.
                var apps = allApps.Where(a => a.TenderId == tender.Id).ToList();

                // Count local suppliers: Supplier type is "local" (case-insensitive).
                int local = apps.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "local");

                // Count foreign suppliers: Supplier type is "foreign" (case-insensitive).
                int foreign = apps.Count(a => (a.OVRS_User?.Supplier ?? "").ToLower() == "foreign");

                // Total number of applicants for this tender.
                int total = local + foreign;

                // Create a summary row for this tender.
                return new ClosedTenderSummaryRow
                {
                    TenderType = tender.TenderType ?? "-",  // Tender type or "-" if null.
                    ClosedTenderCount = 1,                  // Each row represents one tender.
                    LocalSuppliers = local,                 // Number of local suppliers.
                    ForeignSuppliers = foreign,             // Number of foreign suppliers.
                    TotalApplicants = total                 // Total applicants for the tender.
                };
            }).ToList();

            // NOTE: If you want to summarize by TenderType, you could group and sum here using .GroupBy().

            // Generate a PDF summary report using the list of summary rows and the date range.
            var pdfBytes = ClosedTendersSummaryPdfService.GenerateSummaryReport(summaryList, startDate.Value, endDate.Value);

            // Return the generated PDF file as a downloadable file to the user.
            // The file name includes the start and end dates for clarity.
            return File(pdfBytes, "application/pdf", $"ClosedTendersSummary_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.pdf");
        }
    }

}

