using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Models.Tender;

namespace SABC_Phase2.Data
{
    public class Phase2Context : DbContext
    {
        public Phase2Context(DbContextOptions<Phase2Context> options) : base(options)
        {
        }

        public DbSet<Tender> Tenders { get; set; }
        public DbSet<TenderDocument> TenderDocuments { get; set; }

        public DbSet<ScheduledTender> ScheduledTenders { get; set; }

        public DbSet<ScheduledTenderDocument> ScheduledTendersDocuments { get; set; }

        public DbSet<TenderDraft> TenderAdminsDraft { get; set; }
        public DbSet<TenderDraftDocument> TenderAdminsDraftDocuments { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenderDocument>()
                .HasOne(td => td.Tender)
                .WithMany(t => t.Documents)
                .HasForeignKey(td => td.TenderId);


            modelBuilder.Entity<TenderDraft>(entity =>
            {
                entity.Property(e => e.Status).IsRequired(false);
                entity.Property(e => e.Title).IsRequired(false);
                entity.Property(e => e.Description).IsRequired(false);
                entity.Property(e => e.TenderType).IsRequired(false);
                entity.Property(e => e.TenderNumber).IsRequired(false);
                //entity.Property(e => e.UserId).IsRequired(false);
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}