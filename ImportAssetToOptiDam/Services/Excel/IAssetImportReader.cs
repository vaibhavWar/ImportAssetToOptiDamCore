using ImportAssetToOptiDam.Models.Import;

namespace ImportAssetToOptiDam.Services.Excel;

/// <summary>
/// Streams rows from the import spreadsheet as strongly-typed <see cref="AssetImportRow"/> instances.
/// </summary>
public interface IAssetImportReader
{
    IAsyncEnumerable<AssetImportRow> ReadRowsAsync(
        string filePath,
        int sheetIndex,
        CancellationToken cancellationToken = default);
}
