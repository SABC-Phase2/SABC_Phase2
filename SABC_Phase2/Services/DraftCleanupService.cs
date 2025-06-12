using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Data;


namespace SABC_Phase2.Services
{
    public class DraftCleanupService : IDraftCleanupService
    {
        private readonly Phase2Context _context;

        public DraftCleanupService(Phase2Context context)
        {
            _context = context;
        }

        public async Task CleanupPublishedDraftAsync(int draftId)
        {
            try
            {
                var draft = await _context.TenderAdminsDraft
                    .Include(d => d.Documents)
                    .FirstOrDefaultAsync(d => d.Id == draftId);

                if (draft != null)
                {
                    _context.TenderAdminsDraft.Remove(draft);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up draft {draftId}: {ex.Message}");
                throw;
            }
        }
    }
}
