namespace EFP.API.Api.Contracts.Uploads;

/// <summary>
/// Creates an upload session for a source file.
/// </summary>
public sealed record CreateUploadRequest(string FileName, long ContentLength, string ContentType, string Sha256);

/// <summary>
/// Represents an upload session.
/// </summary>
public sealed record CreateUploadResponse(Guid UploadId, string ObjectKey, int PartSize, int ExpectedPartCount, DateTimeOffset ExpiresAt, string CompleteUrl, string AbortUrl);

/// <summary>
/// Represents upload metadata.
/// </summary>
public sealed record UploadResponse(Guid UploadId, string FileName, string ObjectKey, long ContentLength, string ContentType, string State, DateTimeOffset ExpiresAt);

/// <summary>
/// Represents a downloadable file reference.
/// </summary>
public sealed record FileDownloadResponse(Guid FileId, string FileName, string ContentType, long ContentLength, string DownloadUrl, DateTimeOffset ExpiresAt);