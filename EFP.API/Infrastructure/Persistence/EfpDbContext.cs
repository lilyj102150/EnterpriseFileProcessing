using EFP.API.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace EFP.API.Infrastructure.Persistence;

public sealed class EfpDbContext(DbContextOptions<EfpDbContext> options) : DbContext(options)
{
    public DbSet<FileJobEntity> FileJobs => Set<FileJobEntity>();
    public DbSet<FileJobProgressEntity> FileJobProgress => Set<FileJobProgressEntity>();
    public DbSet<StorageObjectEntity> StorageObjects => Set<StorageObjectEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileJobEntity>(entity =>
        {
            entity.ToTable("file_jobs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.JobType).HasConversion<short>();
            entity.Property(item => item.Status).HasConversion<short>();
            entity.Property(item => item.Phase).HasConversion<short>();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.ErrorMessage).HasMaxLength(2048);
            entity.HasIndex(item => new { item.Status, item.Priority, item.CreatedAt });
            entity.HasIndex(item => new { item.Status, item.NextAttemptAt });
            entity.HasIndex(item => new { item.TenantId, item.Status });
            entity.HasIndex(item => item.LeaseExpiresAt);
        });

        modelBuilder.Entity<FileJobProgressEntity>(entity =>
        {
            entity.ToTable("file_job_progress");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Phase).HasConversion<short>();
            entity.HasIndex(item => new { item.JobId, item.Sequence }).IsUnique();
        });

        modelBuilder.Entity<StorageObjectEntity>(entity =>
        {
            entity.ToTable("storage_objects");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Provider).HasConversion<short>();
            entity.Property(item => item.ObjectKey).HasMaxLength(1024).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(512).IsRequired();
            entity.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => item.ExpiresAt);
            entity.HasIndex(item => new { item.TenantId, item.ObjectKey }).IsUnique();
        });

        modelBuilder.Entity<IdempotencyRecordEntity>(entity =>
        {
            entity.ToTable("idempotency_records");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(item => item.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => new { item.TenantId, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => item.ExpiresAt);
        });
    }
}