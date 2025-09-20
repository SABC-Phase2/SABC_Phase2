using Microsoft.AspNetCore.Http;
using SABC_Phase2.Data;
using SABC_Phase2.Models.Tender;
using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    public class AuditLogService
    {
        private readonly Phase2Context _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly SouthAfricanTimeService _saTimeService;

        public AuditLogService(
            Phase2Context context,
            IHttpContextAccessor httpContextAccessor,
            SouthAfricanTimeService saTimeService)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _saTimeService = saTimeService;
        }

        public async Task LogAsync(int adminId, string adminEmail, string adminFullName, string actionType, string description)
        {
            var ip = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
            var log = new AuditLog
            {
                AdminId = adminId,
                AdminEmail = adminEmail,
                AdminFullName = adminFullName,
                ActionType = actionType,
                Description = description,
                Timestamp = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified() // Now using SAST
            };
            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();
        }
    }
}