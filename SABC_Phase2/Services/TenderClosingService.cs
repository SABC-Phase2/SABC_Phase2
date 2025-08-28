using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Service responsible for closing expired tenders.
    /// This service is intended to be called periodically (e.g., via Hangfire)
    /// and will automatically update the status of any open tenders whose
    /// closing date and time have passed, based on the local time zone.
    /// </summary>
    public class TenderClosingService : ITenderClosingService
    {
        private readonly Phase2Context _context;
        private readonly EmailService _emailService;

        /// <summary>
        /// Constructor that receives the database context via dependency injection.
        /// </summary>
        /// <param name="context">Entity Framework Core database context</param>
        public TenderClosingService(Phase2Context context, EmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        /// <summary>
        /// Closes all tenders whose closing date and time have passed.
        /// - Combines the ClosingDate and ClosingTime for each open tender.
        /// - Interprets these as local time (e.g., South Africa time).
        /// - Converts the local closing datetime to UTC for correct comparison.
        /// - If the closing datetime is in the past (relative to now), sets the status to "Closed Tender".
        /// - Saves all changes to the database.
        /// </summary>
        public async Task CloseExpiredTendersAsync()
        {
            var utcNow = DateTime.UtcNow;
            var openTenders = await _context.Tenders
                .Where(t => t.Status == "Open Tender")
                .ToListAsync();

            // Query admin emails once
            var adminEmails = await _context.Administrators
                .Select(a => a.Email)
                .ToListAsync();

            foreach (var tender in openTenders)
            {
                var closeTime = tender.ClosingTime ?? TimeSpan.Zero;
                DateTime localCloseDateTime = tender.ClosingDate.Date + closeTime;
                var localTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
                DateTime closeDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(localCloseDateTime, localTimeZone);

                if (closeDateTimeUtc <= utcNow)
                {
                    tender.Status = "Closed Tender";
                    // Notify admins for this tender
                    await _emailService.SendTenderClosedNotificationAsync(
                        adminEmails,
                        tender.TenderNumber,
                        tender.Title,
                        localCloseDateTime
                    );
                }
            }

            await _context.SaveChangesAsync();
        }
    }
}