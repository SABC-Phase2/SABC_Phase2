using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Defines a contract for publishing tenders that are scheduled to go live.
    /// This is used by Hangfire as a background job to automate tender publishing.
    /// </summary>
    public interface ITenderPublishingService
    {
        /// <summary>
        /// Publishes all scheduled tenders whose scheduled publish time has passed.
        /// This includes:
        /// - Creating a live tender record
        /// - Copying all associated documents from the scheduled entity
        /// - Deleting the scheduled tender entry from the database
        /// </summary>
        /// <returns>A Task representing the asynchronous operation.</returns>
        Task PublishScheduledTendersAsync();
    }
}
