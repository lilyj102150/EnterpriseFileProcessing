using EFP.API.Application.Storage;
using EFP.API.Domain.Jobs;
using EFP.API.Infrastructure.Excel.OpenXml;
using EFP.API.Infrastructure.Pdf;

namespace EFP.API.Application.Jobs;

public sealed class FileJobWorker(IServiceScopeFactory scopeFactory, ILogger<FileJobWorker> logger) : BackgroundService
{
    private readonly string workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.ClaimAsync(workerId, TimeSpan.FromMinutes(5), stoppingToken);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                continue;
            }

            try
            {
                await ProcessAsync(job, scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await queue.ReleaseAsync(job.Id, workerId, CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Job {JobId} failed on worker {WorkerId}", job.Id, workerId);
                await scope.ServiceProvider.GetRequiredService<IJobExecutionStore>().FailAsync(job.Id, "ProcessingFailed", exception.Message, stoppingToken);
            }
        }
    }

    private static async Task ProcessAsync(JobRecord job, IServiceProvider services, CancellationToken cancellationToken)
    {
        var execution = services.GetRequiredService<IJobExecutionStore>();
        if (job.JobType == CFileJobType.ExportExcel)
        {
            var objectKey = $"exports/{job.Id:N}.xlsx";
            await execution.ReportAsync(job.Id, CFileJobPhase.WritingWorkbook, 0, 1, 0, 0, 0, 0, cancellationToken);
            await using var destination = await services.GetRequiredService<IObjectStorage>().OpenWriteAsync(objectKey, cancellationToken);
            var bytes = await services.GetRequiredService<OpenXmlExportWriter>().WriteAsync(destination, "export", cancellationToken);
            await services.GetRequiredService<IArtifactStore>().RegisterAsync(job.Id, $"export-{job.Id:N}.xlsx", objectKey, bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", cancellationToken);
            await execution.ReportAsync(job.Id, CFileJobPhase.FinalizingArtifact, 1, 1, 1, 1, 0, bytes, cancellationToken);
            await execution.CompleteAsync(job.Id, cancellationToken);
            return;
        }

        if (job.JobType == CFileJobType.GeneratePdf)
        {
            var objectKey = $"reports/{job.Id:N}.pdf";
            await execution.ReportAsync(job.Id, CFileJobPhase.RenderingPage, 0, 1, 0, 0, 0, 0, cancellationToken);
            await using var destination = await services.GetRequiredService<IObjectStorage>().OpenWriteAsync(objectKey, cancellationToken);
            var bytes = await services.GetRequiredService<PdfReportWriter>().WriteAsync(destination, "report", cancellationToken);
            await services.GetRequiredService<IArtifactStore>().RegisterAsync(job.Id, $"report-{job.Id:N}.pdf", objectKey, bytes, "application/pdf", cancellationToken);
            await execution.ReportAsync(job.Id, CFileJobPhase.FinalizingArtifact, 1, 1, 1, 1, 0, bytes, cancellationToken);
            await execution.CompleteAsync(job.Id, cancellationToken);
            return;
        }

        if (job.JobType is not (CFileJobType.ImportExcel or CFileJobType.ValidateFile))
        {
            await execution.FailAsync(job.Id, "UnsupportedJobType", $"No processor is registered for {job.JobType}.", cancellationToken);
            return;
        }

        if (job.UploadId is null)
        {
            await execution.FailAsync(job.Id, "InputUploadRequired", "An input upload is required for this job type.", cancellationToken);
            return;
        }

        var upload = await services.GetRequiredService<IUploadStore>().GetAsync(job.UploadId.Value, cancellationToken);
        if (upload is null || upload.State != "Completed")
        {
            await execution.FailAsync(job.Id, "InputUploadUnavailable", "The input upload is not completed.", cancellationToken);
            return;
        }

        await execution.ReportAsync(job.Id, CFileJobPhase.ReadingWorkbook, 0, 0, 0, 0, 0, 0, cancellationToken);
        await using var source = await services.GetRequiredService<IObjectStorage>().OpenReadAsync(upload.ObjectKey, cancellationToken);
        var result = await services.GetRequiredService<OpenXmlWorkbookInspector>().InspectAsync(source, cancellationToken);

        if (result.SheetCount == 0 || result.RowCount == 0)
        {
            await execution.FailAsync(job.Id, "WorkbookEmpty", "The workbook does not contain any readable sheets or rows.", cancellationToken);
            return;
        }

        var acceptedRows = result.RowCount;
        await execution.ReportAsync(job.Id, CFileJobPhase.ValidatingRows, 0, result.RowCount, result.RowCount, acceptedRows, 0, result.BytesRead, cancellationToken);
        await execution.CompleteAsync(job.Id, cancellationToken);
    }
}