using Microsoft.AspNetCore.Http;
using SABC_Phase2.Data;
using SABC_Phase2.Models;
using SABC_Phase2.Models.Tender;
using System.Collections.Generic;
using System.Linq;
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

        // Updated to include role
        public async Task LogAsync(int adminId, string adminEmail, string adminFullName, string role, string actionType, string description)
        {
            var ip = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
            var log = new AuditLog
            {
                AdminId = adminId,
                AdminEmail = adminEmail,
                AdminFullName = adminFullName,
                Role = role, // <-- new column
                ActionType = actionType,
                Description = description,
                Timestamp = _saTimeService.GetCurrentSouthAfricanTime().ToDateTimeUnspecified()
            };
            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Creates a detailed change log comparing original and updated tender values
        /// </summary>
        public string BuildTenderChangeLog(
            Tender originalTender,
            TenderEditDto updatedDto,
            string adminFullName,
            List<string> documentChanges = null)
        {
            var changes = new List<string>();

            // Track tender type changes
            if (originalTender.TenderType != updatedDto.TenderType)
            {
                changes.Add($"Tender Type: '{originalTender.TenderType}' → '{updatedDto.TenderType}'");
            }

            // Track tender number changes
            if (originalTender.TenderNumber != updatedDto.TenderNumber)
            {
                changes.Add($"Tender Number: '{originalTender.TenderNumber}' → '{updatedDto.TenderNumber}'");
            }

            // Track title changes
            if (originalTender.Title != updatedDto.Title)
            {
                changes.Add($"Title: '{originalTender.Title}' → '{updatedDto.Title}'");
            }

            // Track description changes
            if (originalTender.Description != updatedDto.Description)
            {
                var oldDesc = originalTender.Description?.Length > 50
                    ? originalTender.Description.Substring(0, 50) + "..."
                    : originalTender.Description;
                var newDesc = updatedDto.Description?.Length > 50
                    ? updatedDto.Description.Substring(0, 50) + "..."
                    : updatedDto.Description;
                changes.Add($"Description: '{oldDesc}' → '{newDesc}'");
            }

            // Track closing date changes
            if (originalTender.ClosingDate != updatedDto.ClosingDate)
            {
                changes.Add($"Closing Date: {originalTender.ClosingDate:yyyy-MM-dd} → {updatedDto.ClosingDate:yyyy-MM-dd}");
            }

            // Track closing time changes
            if (originalTender.ClosingTime != updatedDto.ClosingTime)
            {
                changes.Add($"Closing Time: '{originalTender.ClosingTime}' → '{updatedDto.ClosingTime}'");
            }

            // Track status changes
            if (originalTender.Status != updatedDto.Status)
            {
                changes.Add($"Status: '{originalTender.Status}' → '{updatedDto.Status}'");
            }

            // Track awarded tender changes
            var originalAwarded = originalTender.AwardedTender?.AwardedCompanyName ?? "";
            var newAwarded = updatedDto.AwardedTender ?? "";
            if (originalAwarded != newAwarded)
            {
                if (string.IsNullOrWhiteSpace(originalAwarded) && !string.IsNullOrWhiteSpace(newAwarded))
                {
                    changes.Add($"Awarded To: '{newAwarded}' (newly awarded)");
                }
                else if (!string.IsNullOrWhiteSpace(originalAwarded) && string.IsNullOrWhiteSpace(newAwarded))
                {
                    changes.Add($"Awarded To: '{originalAwarded}' (award removed)");
                }
                else
                {
                    changes.Add($"Awarded To: '{originalAwarded}' → '{newAwarded}'");
                }
            }

            // Add document changes if provided
            if (documentChanges != null && documentChanges.Any())
            {
                changes.AddRange(documentChanges);
            }

            // Get current South African time
            var saTime = _saTimeService.GetCurrentSouthAfricanTime();
            var timestamp = saTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            // Build final description with admin name and timestamp
            if (changes.Any())
            {
                return $"Tender '{updatedDto.TenderNumber}' modified {string.Join("; ", changes)}";
            }
            else
            {
                return $"Tender '{updatedDto.TenderNumber}' was accessed by {adminFullName} on {timestamp} SAST but no changes were detected";
            }
        }

        /// <summary>
        /// Tracks document changes (additions and deletions)
        /// </summary>
        public List<string> TrackDocumentChanges(
            List<TenderDocument> originalDocs,
            List<int> deletedDocIds,
            List<IFormFile> newDocs,
            bool isAwardedDocs = false)
        {
            var changes = new List<string>();
            var docType = isAwardedDocs ? "Awarded Document" : "Supporting Document";

            // Track deletions
            if (deletedDocIds != null && deletedDocIds.Any())
            {
                var deletedFiles = originalDocs.Where(d => deletedDocIds.Contains(d.Id)).Select(d => d.FileName).ToList();
                if (deletedFiles.Any())
                {
                    changes.Add($"{docType}s Deleted: {string.Join(", ", deletedFiles)}");
                }
            }

            // Track additions
            if (newDocs != null && newDocs.Any())
            {
                var newFileNames = newDocs.Select(f => f.FileName).ToList();
                changes.Add($"{docType}s Added: {string.Join(", ", newFileNames)}");
            }

            return changes;
        }
    }
}