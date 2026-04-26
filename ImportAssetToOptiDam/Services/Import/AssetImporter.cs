using ImportAssetToOptiDam.Configuration;
using ImportAssetToOptiDam.Models.Dam;
using ImportAssetToOptiDam.Models.Import;
using ImportAssetToOptiDam.Services.Dam;
using ImportAssetToOptiDam.Services.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImportAssetToOptiDam.Services.Import;

/// <summary>
/// Orchestrates the end-to-end import pipeline:
///   Excel row → upload URL → file upload → create asset → patch metadata → set custom fields.
/// One row failing never stops the batch — errors are logged with full context and the next row proceeds.
/// </summary>
public sealed class AssetImporter
{
    private readonly IAssetImportReader _reader;
    private readonly IOptimizelyDamClient _damClient;
    private readonly IUploadReportWriter _reportWriter;
    private readonly ImportOptions _importOptions;
    private readonly ILogger<AssetImporter> _logger;

    public AssetImporter(
        IAssetImportReader reader,
        IOptimizelyDamClient damClient,
        IUploadReportWriter reportWriter,
        IOptions<ImportOptions> importOptions,
        ILogger<AssetImporter> logger)
    {
        _reader = reader;
        _damClient = damClient;
        _reportWriter = reportWriter;
        _importOptions = importOptions.Value;
        _logger = logger;
    }

    public async Task<ImportResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var baseDir = AppContext.BaseDirectory;
        var excelPath = Path.Combine(baseDir, _importOptions.ImportFolder, _importOptions.ImportFileName);
        var imagesDir = Path.Combine(baseDir, _importOptions.ImagesFolder);

        _logger.LogInformation(
            "Starting import. Excel={ExcelPath}, ImagesDir={ImagesDir}, Sheet={SheetIndex}",
            excelPath, imagesDir, _importOptions.SheetIndex);

        var fields = await _damClient.GetFieldsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Retrieved {Count} DAM fields from CMP.", fields.Count);

        var folders = await _damClient.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var foldersByPath = BuildFolderPathIndex(folders);
        _logger.LogInformation("Retrieved {Count} DAM folders from CMP.", folders.Count);

        // Index fields by name for O(1) lookup. Names are matched case-insensitively.
        var fieldsByName = fields
            .Where(f => f.IsActive)
            .GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int succeeded = 0, failed = 0;
        var reportEntries = new List<ImportReportEntry>();

        await foreach (var row in _reader.ReadRowsAsync(excelPath, _importOptions.SheetIndex, cancellationToken)
                                         .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Each row gets its own logging scope so all messages from its pipeline
            // are correlated in the log aggregator.
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["ImportRow"] = row.SourceRowNumber,
                ["File"] = row.OldFileName ?? "<unknown>",
            });

            try
            {
                var entry = await ProcessRowAsync(row, imagesDir, fieldsByName, foldersByPath, cancellationToken)
                    .ConfigureAwait(false);
                if (entry is not null)
                {
                    reportEntries.Add(entry);
                }
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw; // let cancellation bubble
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Row {Row} failed: {Message}", row.SourceRowNumber, ex.Message);
            }
        }

        _logger.LogInformation("Import finished. Succeeded={Succeeded}, Failed={Failed}", succeeded, failed);

        // Write the per-asset URL report. Failures here are logged but do not fail the
        // overall import — the import itself was successful, the report is reporting.
        if (reportEntries.Count > 0)
        {
            try
            {
                var outputPath = ResolveOutputPath();
                var written = await _reportWriter
                    .WriteAsync(outputPath, reportEntries, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Upload report written to {Path}.", written);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to write the upload report. The {Count} successful uploads still completed.",
                    reportEntries.Count);
            }
        }
        else
        {
            _logger.LogInformation("No successful uploads — skipping report file.");
        }

        return new ImportResult(succeeded, failed);
    }

    private string ResolveOutputPath()
    {
        // Expand a single {date:...} placeholder against today's local date. Anything else
        // is passed through verbatim — keeps the substitution rule small and predictable.
        var fileName = System.Text.RegularExpressions.Regex.Replace(
            _importOptions.OutputFileName,
            pattern: @"\{date:([^}]+)\}",
            evaluator: m => DateTime.Now.ToString(m.Groups[1].Value,
                                                  System.Globalization.CultureInfo.InvariantCulture));

        return Path.Combine(AppContext.BaseDirectory, _importOptions.OutputFolder, fileName);
    }

    private async Task<ImportReportEntry?> ProcessRowAsync(
        AssetImportRow row,
        string imagesDir,
        IReadOnlyDictionary<string, DamField> fieldsByName,
        IReadOnlyDictionary<string, IReadOnlyList<DamFolder>> foldersByPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.OldFileName))
        {
            throw new InvalidOperationException("OldFileName column is empty — nothing to upload.");
        }

        var filePath = Path.Combine(imagesDir, row.OldFileName);
        var folderId = ResolveFolderId(row, foldersByPath);

        // 1. Get a pre-signed upload URL.
        var uploadInfo = await _damClient.GetUploadUrlAsync(cancellationToken).ConfigureAwait(false);

        // 2. Upload the bytes to storage.
        await _damClient.UploadFileToStorageAsync(uploadInfo, filePath, cancellationToken).ConfigureAwait(false);

        // 3. Register the asset in CMP.
        var title = string.IsNullOrWhiteSpace(row.NewFileName) ? row.OldFileName : row.NewFileName;
        var created = await _damClient.CreateAssetAsync(
            new CreateAssetRequest(uploadInfo.Key, title!, folderId),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Asset registered: Id={AssetId}, Type={AssetType}", created.Id, created.Type);

        // 4. Patch metadata (title/description/alt_text/etc.).
        var isImage = string.Equals(created.Type, "image", StringComparison.OrdinalIgnoreCase);
        var patch = new AssetMetadataPatchRequest
        {
            Title           = row.NewFileName,
            Description     = row.Description,
            AltText         = isImage ? row.AltText : null,
            AttributionText = isImage ? null : row.AltText, // mirror legacy fallback for non-image types
            Tags            = isImage ? ParseTags(row.Tags) : null,
            IsPublic        = true,
            IsArchived      = false,
        };
        var patched = await _damClient
            .PatchAssetMetadataAsync(created.Id, created.Type, patch, cancellationToken)
            .ConfigureAwait(false);

        // 5. Set custom field values from the remaining spreadsheet columns.
        var fieldValues = BuildFieldValues(row, fieldsByName);
        await _damClient.SetAssetFieldsAsync(created.Id, fieldValues, cancellationToken).ConfigureAwait(false);

        // 6. Build the report row from data we already have.
        //
        // The CMP asset response carries a single canonical "url" field. Per the docs:
        //   "If the asset is private, the URL includes a token that expires after a
        //    short time."
        // So one URL per asset, and its public-vs-private flavour follows is_public.
        // We patched is_public=true above, so the URL we got back is a public link;
        // the "Private DAM URL" column is therefore left blank by design (there is
        // no separate private URL to capture for assets we just made public).
        var publicUrl  = patched.IsPublic == true ? patched.Url : null;
        var privateUrl = patched.IsPublic == false ? patched.Url : null;

        return new ImportReportEntry(
            NewFileName:   title!,
            PublicDamUrl:  publicUrl,
            PrivateDamUrl: privateUrl);
    }

    private string ResolveFolderId(
        AssetImportRow row,
        IReadOnlyDictionary<string, IReadOnlyList<DamFolder>> foldersByPath)
    {
        var guidFolderId = row.ResolveFolderId();
        if (!string.IsNullOrWhiteSpace(guidFolderId))
        {
            return guidFolderId;
        }

        if (!string.IsNullOrWhiteSpace(row.DamFolderPath))
        {
            foreach (var lookupKey in GetFolderPathLookupKeys(row.DamFolderPath))
            {
                if (!foldersByPath.TryGetValue(lookupKey, out var matches))
                {
                    continue;
                }

                if (matches.Count == 1)
                {
                    _logger.LogInformation(
                        "Resolved DAM Folder Path '{InputPath}' to folder '{ResolvedPath}' ({FolderId}).",
                        row.DamFolderPath, lookupKey, matches[0].Id);
                    return matches[0].Id;
                }

                var duplicatePaths = string.Join(", ", matches.Select(folder => $"{folder.Name} ({folder.Id})"));
                throw new InvalidOperationException(
                    $"DAM Folder Path '{row.DamFolderPath}' matched multiple folders: {duplicatePaths}. " +
                    "Use a full, unique DAM path or set DAMFolderGuid.");
            }

            var knownPaths = string.Join(" | ", foldersByPath.Keys.OrderBy(path => path).Take(20));
            _logger.LogWarning(
                "DAM Folder Path '{InputPath}' was not found. Known folder path keys sample: {KnownPaths}",
                row.DamFolderPath, knownPaths);

            throw new InvalidOperationException(
                $"DAM Folder Path '{row.DamFolderPath}' was not found in Optimizely DAM. " +
                "Use the complete path shown by DAM, for example 'Assets/Folder/Subfolder'.");
        }

        if (!string.IsNullOrWhiteSpace(_importOptions.DefaultFolderId))
        {
            return _importOptions.DefaultFolderId;
        }

        throw new InvalidOperationException(
            "No DAM Folder Path, valid folder GUID, or DefaultFolderId was supplied. " +
            "Set DAMFolderGuid, set DAM Folder Path, or configure Import:DefaultFolderId.");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DamFolder>> BuildFolderPathIndex(
        IReadOnlyList<DamFolder> folders)
    {
        var foldersById = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Id))
            .GroupBy(folder => folder.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var index = new Dictionary<string, List<DamFolder>>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var path in GetFolderIndexPaths(folder, foldersById))
            {
                foreach (var lookupKey in GetFolderPathLookupKeys(path))
                {
                    AddFolderIndexEntry(index, lookupKey, folder);
                }
            }
        }

        return index.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<DamFolder>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetFolderIndexPaths(
        DamFolder folder,
        IReadOnlyDictionary<string, DamFolder> foldersById)
    {
        if (!string.IsNullOrWhiteSpace(folder.Path))
        {
            yield return folder.Path;
        }

        var pathFromParents = BuildFolderPathFromParents(folder, foldersById);
        if (!string.IsNullOrWhiteSpace(pathFromParents))
        {
            yield return pathFromParents;
        }
    }

    private static string? BuildFolderPathFromParents(
        DamFolder folder,
        IReadOnlyDictionary<string, DamFolder> foldersById)
    {
        var segments = new Stack<string>();
        var current = folder;
        var visitedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (string.IsNullOrWhiteSpace(current.Name) || !visitedIds.Add(current.Id))
            {
                break;
            }

            segments.Push(current.Name);

            if (string.IsNullOrWhiteSpace(current.ParentFolderId) ||
                !foldersById.TryGetValue(current.ParentFolderId, out var parent))
            {
                break;
            }

            current = parent;
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static void AddFolderIndexEntry(
        IDictionary<string, List<DamFolder>> index,
        string lookupKey,
        DamFolder folder)
    {
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return;
        }

        if (!index.TryGetValue(lookupKey, out var matches))
        {
            matches = new List<DamFolder>();
            index[lookupKey] = matches;
        }

        if (!matches.Any(existing => string.Equals(existing.Id, folder.Id, StringComparison.OrdinalIgnoreCase)))
        {
            matches.Add(folder);
        }
    }

    private static IReadOnlyList<string> GetFolderPathLookupKeys(string? path)
    {
        var normalized = NormalizeDamFolderPath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        var keys = new List<string> { normalized };
        var withoutAssetsRoot = RemoveLeadingPathSegment(normalized, "Assets");
        if (!string.Equals(withoutAssetsRoot, normalized, StringComparison.OrdinalIgnoreCase))
        {
            keys.Add(withoutAssetsRoot);
        }

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RemoveLeadingPathSegment(string path, string segmentToRemove)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 ||
            !string.Equals(segments[0], segmentToRemove, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return string.Join('/', segments.Skip(1));
    }

    private static string NormalizeDamFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path
            .Replace('\\', '/')
            .Replace('>', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));

        return string.Join('/', segments);
    }

    private static IReadOnlyList<string>? ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tags = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tags.Count == 0 ? null : tags;
    }

    private static IReadOnlyList<AssetFieldValue> BuildFieldValues(
        AssetImportRow row,
        IReadOnlyDictionary<string, DamField> fieldsByName)
    {
        var result = new List<AssetFieldValue>();

        foreach (var (headerName, rawValue) in row.CustomFieldValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) continue;
            if (!fieldsByName.TryGetValue(headerName.Trim(), out var field)) continue;

            IReadOnlyList<object> values;

            if (field.Choices is { Count: > 0 })
            {
                values = ResolveChoiceValues(rawValue!, field.Choices);
                if (values.Count == 0) continue;
            }
            else if (string.Equals(field.Type, "number", StringComparison.OrdinalIgnoreCase))
            {
                // Only send a number if we can actually parse one; otherwise skip rather than
                // send a malformed payload. Using InvariantCulture to avoid locale surprises.
                if (!decimal.TryParse(rawValue, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    continue;
                }
                values = new object[] { num };
            }
            else
            {
                values = new object[] { rawValue! };
            }

            result.Add(new AssetFieldValue(field.Type, field.Id, values));
        }

        return result;
    }

    /// <summary>
    /// Maps a comma-separated string of choice names to the field's choice IDs.
    /// The literal value "ALL" (case-insensitive) expands to every choice.
    /// </summary>
    private static IReadOnlyList<object> ResolveChoiceValues(string raw, IReadOnlyList<DamFieldChoice> choices)
    {
        if (string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return choices.Select(c => (object)c.Id).ToList();
        }

        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matched = new List<object>();
        foreach (var token in tokens)
        {
            var choice = choices.FirstOrDefault(c =>
                string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
            if (choice is not null) matched.Add(choice.Id);
        }
        return matched;
    }
}

public sealed record ImportResult(int Succeeded, int Failed);
