using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;

namespace EFP.API.Infrastructure.Excel.OpenXml;

public sealed record WorkbookInspectionResult(long SheetCount, long RowCount, long BytesRead);

public sealed class OpenXmlWorkbookInspector
{
    public ValueTask<WorkbookInspectionResult> InspectAsync(Stream source, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(source, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook part is missing.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("Workbook root is missing.");
        var sheetCount = 0L;
        var rowCount = 0L;
        var sheets = workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>();
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workbookPart.GetPartById(sheet.Id?.Value ?? string.Empty) is not WorksheetPart worksheetPart) continue;
            sheetCount++;
            using var reader = OpenXmlReader.Create(worksheetPart);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ElementType == typeof(Row) && reader.IsStartElement) rowCount++;
            }
        }
        return ValueTask.FromResult(new WorkbookInspectionResult(sheetCount, rowCount, source.Position));
    }
}