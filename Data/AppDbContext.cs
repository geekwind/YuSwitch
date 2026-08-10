using Microsoft.EntityFrameworkCore;
using YuSwitch.Data.Entities;

namespace YuSwitch.Data;

/// <summary>
/// EF Core DbContext for the gateway. Default provider is SQLite (zero-config);
/// switch to PostgreSQL/MySQL by changing the connection string + provider package.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ServiceEntity> Services => Set<ServiceEntity>();
    public DbSet<ModelEntity> Models => Set<ModelEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<UsageLogEntity> UsageLogs => Set<UsageLogEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ServiceEntity>(e =>
        {
            e.HasIndex(x => x.ProviderType);
            e.HasIndex(x => x.Enabled);
            e.Property(x => x.CredentialsJson).HasMaxLength(4096);
            e.HasMany(x => x.Models)
             .WithOne(x => x.Service)
             .HasForeignKey(x => x.ServiceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ModelEntity>(e =>
        {
            e.HasIndex(x => x.ModelName);
            e.HasIndex(x => new { x.ServiceId, x.ModelName });
        });

        b.Entity<ApiKeyEntity>(e =>
        {
            e.HasIndex(x => x.KeyValue).IsUnique();
            e.HasIndex(x => x.Enabled);
        });

        b.Entity<UsageLogEntity>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Model);
            e.HasIndex(x => x.ApiKeyName);
        });

        b.Entity<SettingEntity>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });
    }
}
