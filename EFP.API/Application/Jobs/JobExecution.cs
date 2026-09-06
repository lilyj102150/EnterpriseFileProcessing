using EFP.API.Domain.Jobs;
using EFP.API.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EFP.API.Application.Jobs;

public interface IJobExecutionStore
{
    ValueTask ReportAsync(Guid jobId, CFileJobPhase phase, long currentRow, long totalRows, long processedRows, long acceptedRows, long rejectedRows, long bytesRead, CancellationToken cancellationToken);
    ValueTask CompleteAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask FailAsync(Guid jobId, string code, string message, CancellationToken cancellationToken);
}

public sealed class PostgresJobExecutionStore(EfpDbContext db) : IJobExecutionStore
{
    public async ValueTask ReportAsync(Guid jobId, CFileJobPhase phase, long currentRow, long totalRows, long processedRows, long acceptedRows, long rejectedRows, long bytesRead, CancellationToken cancellationToken)
    {
        var job = await db.FileJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
        var sequence = await db.FileJobProgress.Where(item => item.JobId == jobId).Select(item => (long?)item.Sequence).MaxAsync(cancellationToken) ?? 0;
        job.Phase = phase;
        job.CurrentRow = currentRow;
        job.TotalRows = totalRows;
        job.ProcessedRows = processedRows;
        job.AcceptedRows = acceptedRows;
        job.RejectedRows = rejectedRows;
        job.Version++;
        db.FileJobProgress.Add(new FileJobProgressEntity { JobId = jobId, Sequence = sequence + 1, Phase = phase, CurrentRow = currentRow, TotalRows = totalRows, ProcessedRows = processedRows, AcceptedRows = acceptedRows, RejectedRows = rejectedRows, BytesRead = bytesRead, CapturedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask CompleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.FileJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
        job.Status = CFileJobStatus.Succeeded;
        job.Phase = CFileJobPhase.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask FailAsync(Guid jobId, string code, string message, CancellationToken cancellationToken)
    {
        var job = await db.FileJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
        job.Status = CFileJobStatus.Failed;
        job.ErrorCode = code;
        job.ErrorMessage = message[..Math.Min(message.Length, 2048)];
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }
}