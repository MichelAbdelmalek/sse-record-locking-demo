using Microsoft.EntityFrameworkCore;

namespace LockDemo.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WorkRecord> Records => Set<WorkRecord>();
    public DbSet<RecordLock> RecordLocks => Set<RecordLock>();
    public DbSet<TabSession> TabSessions => Set<TabSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<WorkRecord>(e =>
        {
            e.ToTable("records");
            e.Property(r => r.Reference).HasMaxLength(32);
            e.Property(r => r.Title).HasMaxLength(120);
            e.Property(r => r.Notes).HasMaxLength(2000);
            e.Property(r => r.UpdatedBy).HasMaxLength(40);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => r.Reference).IsUnique();
            // Keyset pagination and "next unlocked" walk the queue in this order.
            e.HasIndex(r => new { r.Priority, r.Id });
            e.HasOne(r => r.Lock).WithOne(l => l.Record).HasForeignKey<RecordLock>(l => l.RecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecordLock>(e =>
        {
            e.ToTable("record_locks");
            e.HasKey(l => l.RecordId);
            e.Property(l => l.HolderName).HasMaxLength(40);
            e.HasIndex(l => l.HolderTabId);
        });

        b.Entity<TabSession>(e =>
        {
            e.ToTable("tab_sessions");
            e.HasKey(s => s.TabId);
            e.Property(s => s.Name).HasMaxLength(40);
            e.HasIndex(s => s.PublicId).IsUnique();
            e.HasIndex(s => s.LastSeen);
        });
    }
}
