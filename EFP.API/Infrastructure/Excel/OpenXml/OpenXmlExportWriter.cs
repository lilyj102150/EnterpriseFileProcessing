using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace EFP.API.Infrastructure.Excel.OpenXml;

public sealed class OpenXmlExportWriter
{
    public async ValueTask<long> WriteAsync(Stream destination, string exportCode, CancellationToken cancellationToken)
    {
        using (var document = SpreadsheetDocument.Create(destination, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var relationshipId = workbookPart.GetIdOfPart(worksheetPart);
            workbookPart.Workbook = new Workbook(new Sheets(new Sheet { Name = "Export", SheetId = 1, Id = relationshipId }));
            using var writer = OpenXmlWriter.Create(worksheetPart);
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            WriteRow(writer, 1, "ExportCode", exportCode);
            WriteRow(writer, 2, "GeneratedAt", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteEndElement();
            writer.WriteEndElement();
            workbookPart.Workbook.Save();
        }
        await destination.FlushAsync(cancellationToken);
        return destination.Position;
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowNumber, string name, string value)
    {
        writer.WriteStartElement(new Row { RowIndex = rowNumber });
        writer.WriteElement(new Cell { CellReference = $"A{rowNumber}", DataType = CellValues.String, CellValue = new CellValue(name) });
        writer.WriteElement(new Cell { CellReference = $"B{rowNumber}", DataType = CellValues.String, CellValue = new CellValue(value) });
        writer.WriteEndElement();
    }
}