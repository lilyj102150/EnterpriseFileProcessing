using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EFP.API.Infrastructure.Pdf;

public sealed class PdfReportWriter
{
    public async ValueTask<long> WriteAsync(Stream destination, string reportCode, CancellationToken cancellationToken)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.Content().Column(column =>
            {
                column.Item().Text(reportCode).FontSize(20);
                column.Item().Text($"Generated at {DateTimeOffset.UtcNow:O}");
            });
        }));
        document.GeneratePdf(destination);
        await destination.FlushAsync(cancellationToken);
        return destination.Position;
    }
}