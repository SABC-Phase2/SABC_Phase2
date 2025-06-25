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

        /// <summary>
        /// Constructor that receives the database context via dependency injection.
        /// </summary>
        /// <param name="context">Entity Framework Core database context</param>
        public TenderClosingService(Phase2Context context)
        {
            _context = context;
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
            // Get the current UTC time for accurate comparison
            var utcNow = DateTime.UtcNow;

            // Query all tenders currently marked as "Open Tender"
            // This includes tenders that may or may not have a ClosingTime specified
            var openTenders = await _context.Tenders
                .Where(t => t.Status == "Open Tender")
                .ToListAsync();

            // Process each open tender to determine if it should be closed
            foreach (var tender in openTenders)
            {
                // If ClosingTime is not specified, default to midnight (00:00)
                var closeTime = tender.ClosingTime ?? TimeSpan.Zero;

                // Combine ClosingDate and ClosingTime as a local DateTime
                DateTime localCloseDateTime = tender.ClosingDate.Date + closeTime;

                // Specify the local time zone (update this string if your region is different!)
                var localTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");

                // Convert the local closing datetime to UTC for comparison
                DateTime closeDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(localCloseDateTime, localTimeZone);

                // If the closing datetime (in UTC) is in the past or now, close the tender
                if (closeDateTimeUtc <= utcNow)
                {
                    tender.Status = "Closed Tender";
                }
            }

            // Save all status changes to the database in a single transaction
            await _context.SaveChangesAsync();
        }
    }
}