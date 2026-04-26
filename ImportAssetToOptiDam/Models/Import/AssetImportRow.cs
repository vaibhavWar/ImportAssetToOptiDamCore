namespace ImportAssetToOptiDam.Models.Import;

/// <summary>
/// A single row read from the import spreadsheet, projected into a strong type.
/// Fixed columns that the importer understands are surfaced as properties;
/// everything else lives in <see cref="CustomFieldValues"/> and is matched by
/// header name against the DAM's configured custom fields.
/// </summary>
public sealed record AssetImportRow
{
    /// <summary>1-based index of the source row in the spreadsheet (for error messages).</summary>
    public int SourceRowNumber { get; init; }

    public string? SourceFolderPath { get; init; }
    public string? OldFileName { get; init; }
    public string? NewFileName { get; init; }
    public string? DamFolderGuid { get; init; }
    public string? DamFolderPath { get; init; }
    public string? Description { get; init; }
    public string? AltText { get; init; }
    public string? Tags { get; init; }

    /// <summary>
    /// All columns by header name to cell value. Used when resolving custom DAM fields.
    /// </summary>
    public IReadOnlyDictionary<string, string?> CustomFieldValues { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the DAM folder GUID from the spreadsheet, or null when the column is empty.
    /// </summary>
    public string? ResolveFolderId()
    {
        if (string.IsNullOrWhiteSpace(DamFolderGuid))
        {
            return null;
        }

        var candidate = DamFolderGuid.Trim();
        if (Guid.TryParse(candidate, out _))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            $"DAMFolderGuid value '{DamFolderGuid}' is not a valid GUID.");
    }
}
