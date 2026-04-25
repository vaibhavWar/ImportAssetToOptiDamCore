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
                var entry = await ProcessRowAsync(row, imagesDir, fieldsByName, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.OldFileName))
        {
            throw new InvalidOperationException("OldFileName column is empty — nothing to upload.");
        }

        var filePath = Path.Combine(imagesDir, row.OldFileName);
        var folderId = row.ResolveFolderId() ?? _importOptions.DefaultFolderId;
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new InvalidOperationException(
                "No valid folder GUID found on the row and no DefaultFolderId configured. " +
                "Set Import:DefaultFolderId or supply a GUID in one of the folder columns.");
        }

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
