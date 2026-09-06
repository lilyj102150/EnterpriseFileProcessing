using EFP.API.Application.Jobs;
using EFP.API.Infrastructure.Persistence;
using EFP.API.Application.Storage;
using EFP.API.Infrastructure.Storage;
using EFP.API.Infrastructure.Excel.OpenXml;
using EFP.API.Infrastructure.Pdf;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddDbContext<EfpDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddScoped<IUploadStore, PostgresUploadStore>();
builder.Services.AddScoped<IJobStore, PostgresJobStore>();
builder.Services.AddScoped<IJobQueue>(provider => (IJobQueue)provider.GetRequiredService<IJobStore>());
builder.Services.AddScoped<IProgressStore, PostgresProgressStore>();
builder.Services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
builder.Services.AddScoped<IJobExecutionStore, PostgresJobExecutionStore>();
builder.Services.AddScoped<IArtifactStore, PostgresArtifactStore>();
builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
builder.Services.AddSingleton<OpenXmlWorkbookInspector>();
builder.Services.AddSingleton<OpenXmlExportWriter>();
builder.Services.AddSingleton<PdfReportWriter>();
builder.Services.AddHostedService<FileJobWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<EfpDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapControllers();

app.Run();