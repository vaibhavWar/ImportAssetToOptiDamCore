using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ImportAssetToOptiDam.Models.Import;
using Microsoft.Extensions.Logging;

namespace ImportAssetToOptiDam.Services.Excel;

/// <summary>
/// Reads an .xlsx file using DocumentFormat.OpenXml. Column meaning is derived
/// from the first row's header names rather than from positional magic numbers.
/// </summary>
public sealed partial class OpenXmlAssetImportReader : IAssetImportReader
{
    // Map of header text → logical field on AssetImportRow. Matching is case-insensitive,
    // whitespace-insensitive, and underscore/space-insensitive. If the spreadsheet's header
    // row is reordered, the reader still resolves the right columns.
    private static readonly IReadOnlyDictionary<string, string> KnownHeaderMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceLink"]       = nameof(AssetImportRow.SourceFolderPath),
            ["SourceFolderPath"] = nameof(AssetImportRow.SourceFolderPath),
            ["OldFileName"]      = nameof(AssetImportRow.OldFileName),
            ["NewFileName"]      = nameof(AssetImportRow.NewFileName),
            ["DAMFolderGuid"]    = nameof(AssetImportRow.DamFolderGuid),
            ["DAMFolderPath"]    = nameof(AssetImportRow.DamFolderPath),
            ["Description"]      = nameof(AssetImportRow.Description),
            ["AltText"]          = nameof(AssetImportRow.AltText),
            ["Tags"]             = nameof(AssetImportRow.Tags),
        };

    private readonly ILogger<OpenXmlAssetImportReader> _logger;

    public OpenXmlAssetImportReader(ILogger<OpenXmlAssetImportReader> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<AssetImportRow> ReadRowsAsync(
        string filePath,
        int sheetIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Import file not found: {filePath}", filePath);
        }

        // OpenXml itself is synchronous; the method is async to keep a uniform
        // streaming contract for callers and to honour cancellation between rows.
        // The Task.Yield lets the scheduler interleave and satisfies CS1998 without
        // pretending the underlying I/O is async.
        await Task.Yield();

        using var doc = SpreadsheetDocument.Open(filePath, isEditable: false);
        var workbookPart = doc.WorkbookPart
            ?? throw new InvalidDataException("Workbook part is missing.");
        var sheets = workbookPart.Workbook.GetFirstChild<Sheets>()?.Elements<Sheet>().ToList()
            ?? throw new InvalidDataException("Workbook contains no sheets.");

        if (sheetIndex < 0 || sheetIndex >= sheets.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sheetIndex),
                $"Sheet index {sheetIndex} is out of range (workbook has {sheets.Count} sheets).");
        }

        var sheet = sheets[sheetIndex];
        var sheetId = sheet.Id?.Value
            ?? throw new InvalidDataException("Sheet has no relationship id.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetId);
        var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("Sheet has no data.");

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        Dictionary<int, string>? headerByColumn = null;
        int dataRowNumber = 0;

        foreach (var row in sheetData.Elements<Row>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = MaterializeRowByColumn(row, sharedStrings);

            if (headerByColumn is null)
            {
                headerByColumn = cells.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value ?? string.Empty,
                    EqualityComparer<int>.Default);
                _logger.LogInformation("Detected {Count} header columns.", headerByColumn.Count);
                continue;
            }

            dataRowNumber++;
            yield return BuildImportRow(headerByColumn, cells, dataRowNumber);
        }
    }

    private static AssetImportRow BuildImportRow(
        Dictionary<int, string> headerByColumn,
        Dictionary<int, string?> cells,
        int dataRowNumber)
    {
        string? fixedValue(string logicalName)
        {
            foreach (var (col, header) in headerByColumn)
            {
                if (!KnownHeaderMap.TryGetValue(Normalize(header), out var mapped)) continue;
                if (!mapped.Equals(logicalName, StringComparison.Ordinal)) continue;
                return cells.TryGetValue(col, out var v) ? v : null;
            }
            return null;
        }

        // Build the custom-field dictionary: any header not in KnownHeaderMap goes here verbatim.
        var custom = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, header) in headerByColumn)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            if (KnownHeaderMap.ContainsKey(Normalize(header))) continue;
            custom[header.Trim()] = cells.TryGetValue(col, out var v) ? v : null;
        }

        return new AssetImportRow
        {
            SourceRowNumber    = dataRowNumber,
            SourceFolderPath   = fixedValue(nameof(AssetImportRow.SourceFolderPath)),
            OldFileName        = fixedValue(nameof(AssetImportRow.OldFileName)),
            NewFileName        = fixedValue(nameof(AssetImportRow.NewFileName)),
            DamFolderGuid      = fixedValue(nameof(AssetImportRow.DamFolderGuid)),
            DamFolderPath      = fixedValue(nameof(AssetImportRow.DamFolderPath)),
            Description        = fixedValue(nameof(AssetImportRow.Description)),
            AltText            = fixedValue(nameof(AssetImportRow.AltText)),
            Tags               = fixedValue(nameof(AssetImportRow.Tags)),
            CustomFieldValues  = custom,
        };
    }

    /// <summary>
    /// Materializes a row into a column-index → cell-value dictionary, handling
    /// empty cells, shared-string refs, and inline-string cells uniformly.
    /// Column indices are 1-based to match Excel's "A=1" convention.
    /// </summary>
    private static Dictionary<int, string?> MaterializeRowByColumn(
        Row row, SharedStringTable? sharedStrings)
    {
        var result = new Dictionary<int, string?>();
        foreach (var cell in row.Elements<Cell>())
        {
            var colIndex = ParseColumnIndex(cell.CellReference?.Value);
            if (colIndex < 0) continue;
            result[colIndex] = ExtractCellValue(cell, sharedStrings);
        }
        return result;
    }

    private static string? ExtractCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.CellValue is null && cell.InlineString is null) return null;

        // Inline string (rare but valid).
        if (cell.DataType?.Value == CellValues.InlineString && cell.InlineString?.Text is not null)
        {
            return cell.InlineString.Text.Text;
        }

        // Shared-string index.
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (sharedStrings is null) return null;
            if (!int.TryParse(cell.InnerText, out var idx)) return null;
            var item = sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(idx);
            return item?.InnerText;
        }

        // Boolean.
        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return cell.InnerText == "1" ? "TRUE" : "FALSE";
        }

        // Numeric / date / anything else: return the raw text. We don't attempt date
        // formatting here because the old import was string-based and the API accepts strings.
        return cell.InnerText;
    }

    [GeneratedRegex("^[A-Z]+", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnLettersRegex();

    /// <summary>
    /// Converts a cell reference like "B7" → column index 2 (1-based).
    /// Returns -1 for malformed references.
    /// </summary>
    private static int ParseColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return -1;
        var match = ColumnLettersRegex().Match(cellReference);
        if (!match.Success) return -1;

        int value = 0;
        foreach (var ch in match.Value)
        {
            value = value * 26 + (ch - 'A' + 1);
        }
        return value;
    }

    private static string Normalize(string header) =>
        new(header.Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray());
}
