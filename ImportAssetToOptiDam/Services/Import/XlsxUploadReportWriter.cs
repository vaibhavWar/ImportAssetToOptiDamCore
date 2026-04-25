using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ImportAssetToOptiDam.Models.Import;
using Microsoft.Extensions.Logging;

namespace ImportAssetToOptiDam.Services.Import;

/// <summary>
/// Writes the post-import report as an .xlsx file using DocumentFormat.OpenXml. Avoids
/// pulling in a heavier dependency like EPPlus and matches the library already used to
/// read the input spreadsheet.
/// </summary>
public sealed class XlsxUploadReportWriter : IUploadReportWriter
{
    // Column widths chosen to comfortably fit a typical filename and a CMP URL without
    // wrapping. Excel's width unit is roughly the width of a digit in the workbook's
    // default font; ~80 → ~600px, which fits most CMP URLs.
    private static readonly (string Header, double Width)[] ReportColumns =
    {
        ("New FileName",    36.0),
        ("Public DAM URL",  80.0),
        ("Private DAM URL", 80.0),
    };

    private const uint BodyStyleIndex   = 0;
    private const uint HeaderStyleIndex = 1;

    private readonly ILogger<XlsxUploadReportWriter> _logger;

    public XlsxUploadReportWriter(ILogger<XlsxUploadReportWriter> logger)
    {
        _logger = logger;
    }

    public Task<string> WriteAsync(
        string outputPath,
        IReadOnlyList<ImportReportEntry> entries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var resolvedPath = ResolveNonOverwritingPath(outputPath);
        WriteWorkbook(resolvedPath, entries);

        _logger.LogInformation(
            "Wrote upload report with {Count} row(s) to {Path}.", entries.Count, resolvedPath);

        return Task.FromResult(resolvedPath);
    }

    /// <summary>
    /// If <paramref name="desiredPath"/> exists, append " (2)", " (3)", … before the extension
    /// until we find a free name. Prevents two consecutive runs from clobbering each other.
    /// </summary>
    private static string ResolveNonOverwritingPath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var stem      = Path.GetFileNameWithoutExtension(desiredPath);
        var ext       = Path.GetExtension(desiredPath);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        // Pathological — fall back to a timestamp suffix.
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        return Path.Combine(directory, $"{stem} ({ts}){ext}");
    }

    private static void WriteWorkbook(string path, IReadOnlyList<ImportReportEntry> entries)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet();
        stylesPart.Stylesheet.Save();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();

        var worksheet = new Worksheet();
        worksheet.AppendChild(BuildColumnDefinitions());
        worksheet.AppendChild(sheetData);
        worksheetPart.Worksheet = worksheet;

        // Header row.
        var header = new Row { RowIndex = 1U };
        for (var c = 0; c < ReportColumns.Length; c++)
        {
            header.AppendChild(BuildInlineStringCell(
                cellRef: $"{ColumnLetter(c + 1)}1",
                text: ReportColumns[c].Header,
                styleIndex: HeaderStyleIndex));
        }
        sheetData.AppendChild(header);

        // Data rows.
        for (var r = 0; r < entries.Count; r++)
        {
            var rowIndex = (uint)(r + 2);
            var entry = entries[r];
            var row = new Row { RowIndex = rowIndex };
            row.AppendChild(BuildInlineStringCell($"A{rowIndex}", entry.NewFileName, BodyStyleIndex));
            row.AppendChild(BuildInlineStringCell($"B{rowIndex}", entry.PublicDamUrl ?? string.Empty, BodyStyleIndex));
            row.AppendChild(BuildInlineStringCell($"C{rowIndex}", entry.PrivateDamUrl ?? string.Empty, BodyStyleIndex));
            sheetData.AppendChild(row);
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.AppendChild(new Sheet
        {
            Id      = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name    = "Uploaded Assets",
        });

        workbookPart.Workbook.Save();
    }

    private static Columns BuildColumnDefinitions()
    {
        var cols = new Columns();
        for (var i = 0; i < ReportColumns.Length; i++)
        {
            cols.AppendChild(new Column
            {
                Min         = (uint)(i + 1),
                Max         = (uint)(i + 1),
                Width       = ReportColumns[i].Width,
                CustomWidth = true,
            });
        }
        return cols;
    }

    private static Cell BuildInlineStringCell(string cellRef, string text, uint styleIndex)
        => new()
        {
            CellReference = cellRef,
            DataType      = CellValues.InlineString,
            StyleIndex    = styleIndex,
            InlineString  = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
        };

    /// <summary>Two cell formats: default (index 0) and bold-header (index 1).</summary>
    private static Stylesheet BuildStylesheet() => new(
        new Fonts(
            new Font(),                                              // 0: default
            new Font(new Bold(), new FontSize { Val = 11 })),        // 1: bold
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
        new Borders(new Border()),
        new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
            new CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true }));

    private static string ColumnLetter(int oneBasedIndex)
        => ((char)('A' + oneBasedIndex - 1)).ToString();
}
