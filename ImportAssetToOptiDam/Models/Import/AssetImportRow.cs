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

    public string? SourceLink { get; init; }
    public string? OldFileName { get; init; }
    public string? NewFileName { get; init; }
    public string? ParentFolder { get; init; }
    public string? Subfolder { get; init; }
    public string? Subfolder2 { get; init; }
    public string? Subfolder3 { get; init; }
    public string? Description { get; init; }
    public string? AltText { get; init; }

    /// <summary>
    /// All columns by header name → cell value. Used when resolving custom DAM fields.
    /// </summary>
    public IReadOnlyDictionary<string, string?> CustomFieldValues { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first sub-folder-like value that parses as a GUID,
    /// walking deepest → shallowest. Returns null if none are valid GUIDs.
    /// </summary>
    public string? ResolveFolderId()
    {
        foreach (var candidate in new[] { Subfolder3, Subfolder2, Subfolder, ParentFolder })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Guid.TryParse(candidate.Trim(), out _))
            {
                return candidate.Trim();
            }
        }
        return null;
    }
}
