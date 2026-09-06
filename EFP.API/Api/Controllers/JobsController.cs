using EFP.API.Api.Contracts.Jobs;
using EFP.API.Application.Jobs;
using EFP.API.Domain.Jobs;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

namespace EFP.API.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class JobsController(IJobStore jobs, IProgressStore progress, IIdempotencyStore idempotency) : ControllerBase
{
    [HttpPost("imports")]
    public async Task<ActionResult<CreateJobResponse>> CreateImport(CreateImportRequest request, CancellationToken cancellationToken) => await CreateCommandAsync(CFileJobType.ImportExcel, request.UploadId, request, cancellationToken);
    [HttpPost("exports")]
    public async Task<ActionResult<CreateJobResponse>> CreateExport(CreateExportRequest request, CancellationToken cancellationToken) => await CreateCommandAsync(CFileJobType.ExportExcel, null, request, cancellationToken);
    [HttpPost("reports")]
    public async Task<ActionResult<CreateJobResponse>> CreateReport(CreateReportRequest request, CancellationToken cancellationToken) => await CreateCommandAsync(CFileJobType.GeneratePdf, null, request, cancellationToken);
    [HttpGet("jobs/{jobId:guid}")]
    public async Task<ActionResult<JobResponse>> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        return job is null ? NotFound() : Ok(new JobResponse(job.Id, job.JobType, job.Status, job.Phase, job.CreatedAt));
    }
    [HttpPost("jobs/{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, CancelJobRequest request, CancellationToken cancellationToken) => await jobs.CancelAsync(jobId, cancellationToken) is null ? NotFound() : NoContent();
    [HttpGet("jobs/{jobId:guid}/progress")]
    public async Task<ActionResult<JobProgressResponse>> Progress(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null) return NotFound();
        var snapshot = await progress.GetAsync(jobId, cancellationToken);
        return Ok(new JobProgressResponse(snapshot.JobId, snapshot.Status, snapshot.Phase, ProgressMath.CalculatePercent(snapshot.ProcessedRows, snapshot.TotalRows), snapshot.CurrentRow, snapshot.TotalRows, snapshot.ProcessedRows, snapshot.AcceptedRows, snapshot.RejectedRows, snapshot.BytesRead, snapshot.InputBytes, snapshot.BytesWritten, snapshot.OutputBytes, snapshot.PagesRendered, snapshot.TotalPages, snapshot.SheetIndex, snapshot.SheetTotal, snapshot.SheetName, null, null, null, snapshot.Sequence, snapshot.CapturedAt));
    }
    [HttpGet("jobs/{jobId:guid}/progress/events")]
    public Task<ActionResult<JobProgressResponse>> ProgressEvents(Guid jobId, CancellationToken cancellationToken) => Progress(jobId, cancellationToken);
    private async Task<CreateJobResponse> CreateAsync(CFileJobType type, Guid? uploadId, CancellationToken cancellationToken)
    {
        var job = await jobs.CreateAsync(type, 1, uploadId, cancellationToken);
        return new CreateJobResponse(job.Id, job.Status, $"/api/v1/jobs/{job.Id}", $"/api/v1/jobs/{job.Id}/progress");
    }

    private async Task<ActionResult<CreateJobResponse>> CreateCommandAsync<TRequest>(CFileJobType type, Guid? uploadId, TRequest request, CancellationToken cancellationToken)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return BadRequest(new { code = "IdempotencyKeyRequired", message = "A valid Idempotency-Key header is required." });
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        var existing = await idempotency.GetAsync(1, key, hash, cancellationToken);
        if (existing is { Conflict: true }) return Conflict(new { code = "IdempotencyConflict", message = "The idempotency key was already used with a different request." });
        if (existing is not null) return Accepted(new CreateJobResponse(existing.ResourceId, CFileJobStatus.Pending, $"/api/v1/jobs/{existing.ResourceId}", $"/api/v1/jobs/{existing.ResourceId}/progress"));
        var response = await CreateAsync(type, uploadId, cancellationToken);
        await idempotency.CreateAsync(1, key, hash, type.ToString(), response.JobId, cancellationToken);
        return Accepted(response);
    }
}