using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Data;

public sealed class AstraDbContext(DbContextOptions<AstraDbContext> options) : DbContext(options)
{
    public DbSet<NotificationPreferenceEntity> NotificationPreferences => Set<NotificationPreferenceEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Existing SQL Server tables remain migration-owned. Never create or
        // migrate a database automatically from application startup.
        modelBuilder.Entity<NotificationPreferenceEntity>().Property(entity => entity.UserId).ValueGeneratedNever();
        modelBuilder.Entity<NotificationEntity>().Property(entity => entity.Id).ValueGeneratedNever();
    }
}
