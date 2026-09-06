using EFP.API.Api.Contracts.Uploads;
using EFP.API.Application.Storage;
using Microsoft.AspNetCore.Mvc;

namespace EFP.API.Api.Controllers;

[ApiController]
[Route("api/v1/files")]
public sealed class FilesController(IArtifactStore artifacts, IObjectStorage storage) : ControllerBase
{
    [HttpGet("{fileId:guid}")]
    public async Task<ActionResult<FileDownloadResponse>> Get(Guid fileId, CancellationToken cancellationToken)
    {
        var artifact = await artifacts.GetAsync(fileId, cancellationToken);
        if (artifact is null || artifact.State != "Completed") return NotFound();
        return Ok(new FileDownloadResponse(artifact.Id, artifact.FileName, artifact.ContentType, artifact.ContentLength, $"/api/v1/files/{artifact.Id}/content", artifact.ExpiresAt));
    }

    [HttpGet("{fileId:guid}/content")]
    public async Task<IActionResult> Content(Guid fileId, CancellationToken cancellationToken)
    {
        var artifact = await artifacts.GetAsync(fileId, cancellationToken);
        if (artifact is null || artifact.State != "Completed") return NotFound();
        var stream = await storage.OpenReadAsync(artifact.ObjectKey, cancellationToken);
        return File(stream, artifact.ContentType, artifact.FileName, enableRangeProcessing: true);
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Delete(Guid fileId, CancellationToken cancellationToken)
    {
        var artifact = await artifacts.GetAsync(fileId, cancellationToken);
        if (artifact is null) return NotFound();
        await storage.DeleteAsync(artifact.ObjectKey, cancellationToken);
        await artifacts.DeleteAsync(fileId, cancellationToken);
        return NoContent();
    }
}