using Microsoft.AspNetCore.Mvc;
using SABC_Phase2.Services;
using System.Globalization;

namespace SABC_Phase2.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TimeController : ControllerBase
    {
        private readonly SouthAfricanTimeService _saTimeService;

        public TimeController(SouthAfricanTimeService saTimeService)
        {
            _saTimeService = saTimeService;
        }

        [HttpGet("sast-now")]
        public IActionResult GetSouthAfricanTime()
        {
            var saTime = _saTimeService.GetCurrentSouthAfricanTime();
            return Ok(new
            {
                time = saTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            });
        }

    }
}
