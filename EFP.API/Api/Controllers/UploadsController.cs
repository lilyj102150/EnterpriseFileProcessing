using EFP.API.Api.Contracts.Uploads;
using EFP.API.Application.Jobs;
using EFP.API.Application.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace EFP.API.Api.Controllers;

[ApiController]
[Route("api/v1/uploads")]
public sealed class UploadsController(IUploadStore uploads, IObjectStorage storage) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<CreateUploadResponse>> Create(CreateUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength <= 0 || string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.Sha256)) return BadRequest(new { code = "InvalidUpload", message = "FileName, ContentLength, and Sha256 are required." });
        var upload = await uploads.CreateAsync(request.FileName, request.ContentLength, request.ContentType, request.Sha256, cancellationToken);
        return Accepted(new CreateUploadResponse(upload.Id, upload.ObjectKey, 8 * 1024 * 1024, (int)Math.Ceiling((double)upload.ContentLength / (8 * 1024 * 1024)), upload.ExpiresAt, $"/api/v1/uploads/{upload.Id}/complete", $"/api/v1/uploads/{upload.Id}"));
    }
    [HttpGet("{uploadId:guid}")]
    public async Task<ActionResult<UploadResponse>> Get(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await uploads.GetAsync(uploadId, cancellationToken);
        return upload is null ? NotFound() : Ok(new UploadResponse(upload.Id, upload.FileName, upload.ObjectKey, upload.ContentLength, upload.ContentType, upload.State, upload.ExpiresAt));
    }

    [HttpPut("{uploadId:guid}/content")]
    public async Task<IActionResult> UploadContent(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await uploads.GetAsync(uploadId, cancellationToken);
        if (upload is null) return NotFound();
        await using var destination = await storage.OpenWriteAsync(upload.ObjectKey, cancellationToken);
        await Request.Body.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        return NoContent();
    }
    [HttpPost("{uploadId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await uploads.GetAsync(uploadId, cancellationToken);
        if (upload is null) return NotFound();
        if (!await storage.ExistsAsync(upload.ObjectKey, cancellationToken)) return Conflict(new { code = "UploadContentMissing", message = "Upload content has not been received." });
        await using var source = await storage.OpenReadAsync(upload.ObjectKey, cancellationToken);
        if (source.Length != upload.ContentLength) return Conflict(new { code = "UploadLengthMismatch", message = "Uploaded content length does not match the declared length." });
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
        if (!checksum.Equals(upload.Sha256, StringComparison.OrdinalIgnoreCase)) return Conflict(new { code = "UploadChecksumMismatch", message = "Uploaded content checksum does not match the declared checksum." });
        return await uploads.CompleteAsync(uploadId, cancellationToken) ? NoContent() : Conflict(new { code = "UploadStateConflict", message = "Upload cannot be completed in its current state." });
    }
    [HttpDelete("{uploadId:guid}")]
    public async Task<IActionResult> Abort(Guid uploadId, CancellationToken cancellationToken) => await uploads.AbortAsync(uploadId, cancellationToken) ? NoContent() : NotFound();
}