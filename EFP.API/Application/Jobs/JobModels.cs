using EFP.API.Domain.Jobs;

namespace EFP.API.Application.Jobs;

public sealed record JobRecord(Guid Id, long TenantId, CFileJobType JobType, CFileJobStatus Status, CFileJobPhase Phase, DateTimeOffset CreatedAt, Guid? UploadId = null);

public sealed record ProgressRecord(Guid JobId, CFileJobStatus Status, CFileJobPhase Phase, long? CurrentRow, long? TotalRows, long ProcessedRows, long AcceptedRows, long RejectedRows, long BytesRead, long? InputBytes, long BytesWritten, long? OutputBytes, long PagesRendered, long? TotalPages, int? SheetIndex, int? SheetTotal, string? SheetName, long Sequence, DateTimeOffset CapturedAt);

public static class ProgressMath
{
    public static decimal? CalculatePercent(long current, long? total)
    {
        if (total is null || total <= 0) return null;
        if (current <= 0) return 0m;
        if (current >= total.Value) return 100m;
        return Math.Round(current * 100m / total.Value, 6, MidpointRounding.ToEven);
    }
}