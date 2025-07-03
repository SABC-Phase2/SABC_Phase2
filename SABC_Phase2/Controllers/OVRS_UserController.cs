using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;
using SABC_Phase2.Services;
using System.Collections.Generic;
using System.Reflection;

namespace SABC_Phase2.Controllers
{
    public class OVRS_UserController : Controller
    {

        private readonly Phase2Context _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenderAdminController"/> class.
        /// </summary>
        public OVRS_UserController(Phase2Context context, IConfiguration configuration, IWebHostEnvironment env)
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

        }
        public IActionResult Index(string status, int page = 1, int pageSize = 9)
        {
            var query = _context.Tenders.Include(t => t.Documents).AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                // Adjust this logic if your tender.Status values differ
                // Normalize status for comparison
                string statusFilter = status.Trim().ToLower();
                query = query.Where(t => t.Status.ToLower().Contains(statusFilter));
            }

            var totalItems = query.Count();

            var tenders = query
                .OrderByDescending(t => t.DatePublished)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // ... (Blob storage code, ViewBag setup, etc.)

            return View(tenders);
        }


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


        public IActionResult Tender_Application(int id)
        {
            var tender = _context.Tenders.FirstOrDefault(t => t.Id == id);
            if (tender == null)
                return NotFound();
            return View(tender);
        }

        public IActionResult OVRS_Documents()
        {
            return View();
        }

    }
}
