using Microsoft.EntityFrameworkCore;
using SABC_Phase2.Models.Phase1_LegacyDB;

namespace SABC_Phase2.Data
{
    // Legacy/authentication DB context (Phase 1)
    public class LegacyDbContext : DbContext
    {
        public LegacyDbContext(DbContextOptions<LegacyDbContext> options) : base(options) { }

        public DbSet<TblUsers> TblUsers { get; set; }
        public DbSet<TblSuppliers> TblSuppliers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // If your table names in the legacy DB are exactly as specified (case-sensitive, with underscores),
            // otherwise you can specify them explicitly:
            modelBuilder.Entity<TblUsers>().ToTable("tbl_users");
            modelBuilder.Entity<TblSuppliers>().ToTable("tbl_suppliers");

        }
    }
}