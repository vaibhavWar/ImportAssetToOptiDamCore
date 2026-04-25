using System.ComponentModel.DataAnnotations;

namespace ImportAssetToOptiDam.Configuration;

public sealed class ImportOptions
{
    public const string SectionName = "Import";

    [Required]
    public string ImportFileName { get; init; } = default!;

    [Required]
    public string ImportFolder { get; init; } = "ExcelSheet";

    [Required]
    public string ImagesFolder { get; init; } = "Images";

    [Range(0, 100)]
    public int SheetIndex { get; init; } = 0;

    /// <summary>
    /// Folder GUID to use when the spreadsheet row does not supply a valid folder.
    /// If null, rows without a folder are rejected (fail-fast), rather than silently
    /// landing in a hard-coded test folder.
    /// </summary>
    public string? DefaultFolderId { get; init; }

    [Range(1, 32)]
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Folder under the application base directory where the upload-report .xlsx is written.
    /// </summary>
    [Required]
    public string OutputFolder { get; init; } = "Output";

    /// <summary>
    /// File name (without path) used for the upload-report .xlsx. Existing files of the
    /// same name will not be overwritten — the writer disambiguates with a numeric suffix.
    /// Supports the date placeholder <c>{date:yyyyMMdd}</c> for day-stamped reports.
    /// </summary>
    [Required]
    public string OutputFileName { get; init; } = "UploadReport-{date:yyyyMMdd}.xlsx";
}
