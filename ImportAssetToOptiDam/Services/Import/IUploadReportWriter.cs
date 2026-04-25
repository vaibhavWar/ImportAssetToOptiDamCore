using ImportAssetToOptiDam.Models.Import;

namespace ImportAssetToOptiDam.Services.Import;

/// <summary>
/// Writes a post-import report listing every successfully uploaded asset.
/// </summary>
public interface IUploadReportWriter
{
    /// <summary>
    /// Writes <paramref name="entries"/> to <paramref name="outputPath"/>. Returns the
    /// path actually written (which may be the input path or, if the file already exists,
    /// a uniquified sibling so we never silently overwrite an earlier report).
    /// </summary>
    Task<string> WriteAsync(
        string outputPath,
        IReadOnlyList<ImportReportEntry> entries,
        CancellationToken cancellationToken = default);
}
