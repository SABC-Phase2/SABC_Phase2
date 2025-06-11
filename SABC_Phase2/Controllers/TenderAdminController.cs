using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;

namespace SABC_Phase2.Controllers
{
    public class TenderAdminController : Controller
    {
        private readonly Phase2Context _context;
        private readonly IConfiguration _configuration;

        public TenderAdminController(Phase2Context context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // GET: /TenderAdmin/Create
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }

        // POST: /TenderAdmin/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(TenderViewModel model)
        {
            if (!ModelState.IsValid)
            {
                Console.WriteLine("ModelState is invalid:");
                foreach (var kvp in ModelState)
                {
                    foreach (var error in kvp.Value.Errors)
                    {
                        Console.WriteLine($"- {kvp.Key}: {error.ErrorMessage}");
                    }
                }
                return View(model);
            }

            var tender = new Tender
            {
                TenderType = model.TenderType,
                TenderNumber = model.TenderNumber,
                ClosingDate = model.ClosingDate,
                ClosingTime = model.ClosingTime, // <-- Add this line
                Status = model.Status,
                Title = model.Title,
                Description = model.Description,
                DatePublished = DateTime.Now,
                Documents = new List<TenderDocument>()
            };

            _context.Tenders.Add(tender);
            await _context.SaveChangesAsync();

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            if (model.UploadedFiles != null && model.UploadedFiles.Any())
            {
                Console.WriteLine("Uploading documents...");
                foreach (var file in model.UploadedFiles)
                {
                    if (file != null && file.Length > 0)
                    {
                        var blobFileName = $"{tender.Id}/{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";

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

                await _context.SaveChangesAsync();

            }

            return RedirectToAction("Index");
        }



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

            var blobService = new BlobStorageService(_configuration["AzureBlobStorage:ConnectionString"]);

            foreach (var tender in tenders)
            {
                foreach (var doc in tender.Documents)
                {
                    doc.FilePath = blobService.GetBlobSasUri(doc.FilePath);
                }
            }

            return View(tenders);
        }




    }
}


