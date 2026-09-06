using EFP.API.Domain.Jobs;
using EFP.API.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EFP.API.Application.Jobs;

public interface IJobStore { ValueTask<JobRecord> CreateAsync(CFileJobType jobType, long tenantId, Guid? uploadId, CancellationToken cancellationToken); ValueTask<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken); ValueTask<JobRecord?> CancelAsync(Guid id, CancellationToken cancellationToken); }
public interface IJobQueue { ValueTask<JobRecord?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken); ValueTask ReleaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken); }
public interface IProgressStore { ValueTask<ProgressRecord> GetAsync(Guid jobId, CancellationToken cancellationToken); }
public interface IUploadStore { ValueTask<UploadRecord> CreateAsync(string fileName, long contentLength, string contentType, string sha256, CancellationToken cancellationToken); ValueTask<UploadRecord?> GetAsync(Guid id, CancellationToken cancellationToken); ValueTask<bool> CompleteAsync(Guid id, CancellationToken cancellationToken); ValueTask<bool> AbortAsync(Guid id, CancellationToken cancellationToken); }
public interface IIdempotencyStore { ValueTask<IdempotencyResult?> GetAsync(long tenantId, string key, string requestHash, CancellationToken cancellationToken); ValueTask<IdempotencyResult> CreateAsync(long tenantId, string key, string requestHash, string resourceType, Guid resourceId, CancellationToken cancellationToken); }
public sealed record IdempotencyResult(Guid ResourceId, bool Conflict);

public sealed record UploadRecord(Guid Id, string FileName, string ObjectKey, long ContentLength, string ContentType, string Sha256, DateTimeOffset ExpiresAt, string State);

public sealed class PostgresUploadStore(EfpDbContext db) : IUploadStore
{
    public async ValueTask<UploadRecord> CreateAsync(string fileName, long contentLength, string contentType, string sha256, CancellationToken cancellationToken)
    {
        var upload = new StorageObjectEntity { Id = Guid.NewGuid(), TenantId = 1, ObjectKey = $"uploads/{Guid.NewGuid():N}", FileName = fileName, Provider = CFileStorageProvider.Local, ContentLength = contentLength, Sha256 = sha256, ContentType = contentType, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(2), State = "Created" };
        await db.StorageObjects.AddAsync(upload, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToRecord(upload);
    }
    public async ValueTask<UploadRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var upload = await db.StorageObjects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return upload is null ? null : ToRecord(upload);
    }
    public async ValueTask<bool> CompleteAsync(Guid id, CancellationToken cancellationToken) => await TransitionAsync(id, "Completed", cancellationToken);
    public async ValueTask<bool> AbortAsync(Guid id, CancellationToken cancellationToken) => await TransitionAsync(id, "Aborted", cancellationToken);
    private async ValueTask<bool> TransitionAsync(Guid id, string state, CancellationToken cancellationToken)
    {
        var upload = await db.StorageObjects.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (upload is null || upload.State is "Completed" or "Aborted") return false;
        upload.State = state;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
    private static UploadRecord ToRecord(StorageObjectEntity upload) => new(upload.Id, upload.FileName, upload.ObjectKey, upload.ContentLength, upload.ContentType, upload.Sha256, upload.ExpiresAt ?? upload.CreatedAt, upload.State);
}

public sealed class PostgresJobStore(EfpDbContext db) : IJobStore, IJobQueue
{
    public async ValueTask<JobRecord> CreateAsync(CFileJobType jobType, long tenantId, Guid? uploadId, CancellationToken cancellationToken)
    {
        var job = new FileJobEntity { Id = Guid.NewGuid(), TenantId = tenantId, JobType = jobType, Status = CFileJobStatus.Pending, Phase = CFileJobPhase.Queued, CreatedAt = DateTimeOffset.UtcNow, Priority = (int)CJobPriority.Normal, MaxAttempt = 3, UploadId = uploadId };
        await db.FileJobs.AddAsync(job, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToRecord(job);
    }
    public async ValueTask<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => (await db.FileJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken)) is { } job ? ToRecord(job) : null;
    public async ValueTask<JobRecord?> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.FileJobs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (job is null || job.Status is CFileJobStatus.Succeeded or CFileJobStatus.Failed or CFileJobStatus.Cancelled) return null;
        job.Status = CFileJobStatus.Cancelled;
        job.CancelRequestedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToRecord(job);
    }

    public async ValueTask<JobRecord?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.FileJobs.FromSqlRaw("""
            SELECT * FROM file_jobs
            WHERE "Status" IN (0, 1)
              AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= now())
              AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" < now())
            ORDER BY "Priority" DESC, "CreatedAt"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        job.Status = CFileJobStatus.Running;
        job.Phase = CFileJobPhase.Inspecting;
        job.LeaseOwner = workerId;
        job.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
        job.StartedAt ??= DateTimeOffset.UtcNow;
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(job);
    }

    public async ValueTask ReleaseAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        await db.FileJobs.Where(job => job.Id == jobId && job.LeaseOwner == workerId).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Status, CFileJobStatus.Pending)
            .SetProperty(job => job.LeaseOwner, (string?)null)
            .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(job => job.Version, job => job.Version + 1), cancellationToken);
    }
    private static JobRecord ToRecord(FileJobEntity job) => new(job.Id, job.TenantId, job.JobType, job.Status, job.Phase, job.CreatedAt, job.UploadId);
}

public sealed class PostgresProgressStore(EfpDbContext db) : IProgressStore
{
    public async ValueTask<ProgressRecord> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var item = await db.FileJobProgress.AsNoTracking().Where(progress => progress.JobId == jobId).OrderByDescending(progress => progress.Sequence).FirstOrDefaultAsync(cancellationToken);
        var job = await db.FileJobs.AsNoTracking().SingleAsync(current => current.Id == jobId, cancellationToken);
        if (item is null) return new ProgressRecord(jobId, job.Status, job.Phase, null, null, job.ProcessedRows, job.AcceptedRows, job.RejectedRows, 0, job.InputBytes, 0, job.OutputBytes, 0, null, null, null, null, 0, DateTimeOffset.UtcNow);
        return new ProgressRecord(jobId, job.Status, item.Phase, item.CurrentRow, item.TotalRows, item.ProcessedRows, item.AcceptedRows, item.RejectedRows, item.BytesRead, job.InputBytes, item.BytesWritten, job.OutputBytes, 0, null, null, null, null, item.Sequence, item.CapturedAt);
    }
}

public sealed class PostgresIdempotencyStore(EfpDbContext db) : IIdempotencyStore
{
    public async ValueTask<IdempotencyResult?> GetAsync(long tenantId, string key, string requestHash, CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IdempotencyKey == key, cancellationToken);
        if (record is null) return null;
        return new IdempotencyResult(record.ResourceId, record.RequestHash != requestHash);
    }

    public async ValueTask<IdempotencyResult> CreateAsync(long tenantId, string key, string requestHash, string resourceType, Guid resourceId, CancellationToken cancellationToken)
    {
        var record = new IdempotencyRecordEntity { TenantId = tenantId, IdempotencyKey = key, RequestHash = requestHash, ResourceType = resourceType, ResourceId = resourceId, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        await db.IdempotencyRecords.AddAsync(record, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new IdempotencyResult(resourceId, false);
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            var existing = await db.IdempotencyRecords.AsNoTracking().SingleAsync(item => item.TenantId == tenantId && item.IdempotencyKey == key, cancellationToken);
            return new IdempotencyResult(existing.ResourceId, existing.RequestHash != requestHash);
        }
    }
}