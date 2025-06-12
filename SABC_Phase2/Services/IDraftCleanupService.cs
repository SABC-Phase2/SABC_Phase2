namespace SABC_Phase2.Services
{
    public interface IDraftCleanupService
    {
        Task CleanupPublishedDraftAsync(int draftId);
    }
}
