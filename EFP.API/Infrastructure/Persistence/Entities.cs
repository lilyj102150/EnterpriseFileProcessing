using EFP.API.Domain.Jobs;

namespace EFP.API.Infrastructure.Persistence;

public sealed class FileJobEntity
{
    public Guid Id { get; set; }
    public long TenantId { get; set; }
    public CFileJobType JobType { get; set; }
    public CFileJobStatus Status { get; set; }
    public CFileJobPhase Phase { get; set; }
    public int Priority { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempt { get; set; }
    public long? InputBytes { get; set; }
    public long? OutputBytes { get; set; }
    public long? CurrentRow { get; set; }
    public long? TotalRows { get; set; }
    public long AcceptedRows { get; set; }
    public long RejectedRows { get; set; }
    public long ProcessedRows { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? InputObjectKey { get; set; }
    public string? OutputObjectKey { get; set; }
    public long Version { get; set; }
    public Guid? UploadId { get; set; }
}

public sealed class FileJobProgressEntity
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public long Sequence { get; set; }
    public CFileJobPhase Phase { get; set; }
    public long? CurrentRow { get; set; }
    public long? TotalRows { get; set; }
    public long ProcessedRows { get; set; }
    public long AcceptedRows { get; set; }
    public long RejectedRows { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed class StorageObjectEntity
{
    public Guid Id { get; set; }
    public long TenantId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public CFileStorageProvider Provider { get; set; }
    public long ContentLength { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string State { get; set; } = "Created";
}

public sealed class IdempotencyRecordEntity
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}