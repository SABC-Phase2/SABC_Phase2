using System.Threading.Tasks;

namespace SABC_Phase2.Services
{
    public interface ITenderPublishingService
    {
        Task PublishScheduledTendersAsync();
    }
}