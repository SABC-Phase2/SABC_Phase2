using Microsoft.AspNetCore.Mvc;

namespace SABC_Phase2.Controllers
{
    public class TenderAdminController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        // GET: /TenderAdmin/Create
        [HttpGet]
        public IActionResult Create()
        {
            return View();
        }
    }
}
