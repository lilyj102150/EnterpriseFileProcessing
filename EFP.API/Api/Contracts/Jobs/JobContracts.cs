using EFP.API.Domain.Jobs;

namespace EFP.API.Api.Contracts.Jobs;

/// <summary>
/// Creates an import job.
/// </summary>
public sealed record CreateImportRequest(Guid UploadId, string? TemplateCode, bool ValidateOnly);

/// <summary>
/// Creates an export job.
/// </summary>
public sealed record CreateExportRequest(string ExportCode, string? TemplateCode);

/// <summary>
/// Creates a report job.
/// </summary>
public sealed record CreateReportRequest(string ReportCode, string? TemplateCode);

/// <summary>
/// Requests cooperative cancellation for a job.
/// </summary>
public sealed record CancelJobRequest(string Reason);

/// <summary>
/// Represents a newly created job.
/// </summary>
public sealed record CreateJobResponse(Guid JobId, CFileJobStatus Status, string StatusUrl, string ProgressUrl);

/// <summary>
/// Represents a durable job snapshot.
/// </summary>
public sealed record JobResponse(Guid JobId, CFileJobType JobType, CFileJobStatus Status, CFileJobPhase Phase, DateTimeOffset CreatedAt);

/// <summary>
/// Represents current durable job progress.
/// </summary>
public sealed record JobProgressResponse(Guid JobId, CFileJobStatus Status, CFileJobPhase Phase, decimal? Percent, long? CurrentRow, long? TotalRows, long ProcessedRows, long AcceptedRows, long RejectedRows, long BytesRead, long? InputBytes, long BytesWritten, long? OutputBytes, long PagesRendered, long? TotalPages, int? SheetIndex, int? SheetTotal, string? SheetName, decimal? RowsPerSecond, decimal? BytesPerSecond, long? EtaSeconds, long Sequence, DateTimeOffset CapturedAt);