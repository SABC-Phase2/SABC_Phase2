using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Defines the contract for a service that closes expired tenders.
    /// Implementations should provide logic to check all open tenders,
    /// determine if their closing date and time have passed, and update their status accordingly.
    /// </summary>
    public interface ITenderClosingService
    {
        /// <summary>
        /// Closes all expired tenders by updating their status.
        /// This method should:
        /// - Query for open tenders.
        /// - Determine if each tender's closing date and time have passed.
        /// - Update the status of expired tenders (e.g., from "Open Tender" to "Closed Tender").
        /// - Persist changes to the database.
        /// Intended to be called by background jobs (e.g., Hangfire) or on demand.
        /// </summary>
        Task CloseExpiredTendersAsync();
    }
}