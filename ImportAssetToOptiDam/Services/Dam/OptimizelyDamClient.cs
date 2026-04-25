using System.Net.Http.Json;
using System.Text.Json;
using ImportAssetToOptiDam.Configuration;
using ImportAssetToOptiDam.Models.Dam;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImportAssetToOptiDam.Services.Dam;

/// <summary>
/// Typed HttpClient wrapper around the Optimizely CMP DAM v3 API.
/// </summary>
public sealed class OptimizelyDamClient : IOptimizelyDamClient
{
    public const string HttpClientName = nameof(OptimizelyDamClient);
    public const string StorageHttpClientName = "StorageUpload";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OptimizelyCmpOptions _options;
    private readonly ILogger<OptimizelyDamClient> _logger;

    public OptimizelyDamClient(
        HttpClient httpClient,
        IHttpClientFactory httpClientFactory,
        IOptions<OptimizelyCmpOptions> options,
        ILogger<OptimizelyDamClient> logger)
    {
        _httpClient = httpClient;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DamField>> GetFieldsAsync(CancellationToken cancellationToken = default)
    {
        var path = $"/{_options.ApiVersion}/fields?offset=0&page_size={_options.FieldsPageSize}";
        var response = await _httpClient
            .GetFromJsonAsync<DamFieldsResponse>(path, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return response?.Data ?? Array.Empty<DamField>();
    }

    public async Task<UploadUrlResponse> GetUploadUrlAsync(CancellationToken cancellationToken = default)
    {
        var path = $"/{_options.ApiVersion}/upload-url";
        var upload = await _httpClient
            .GetFromJsonAsync<UploadUrlResponse>(path, SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty body from /upload-url.");
        return upload;
    }

    public async Task UploadFileToStorageAsync(
        UploadUrlResponse uploadInfo,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Asset file not found on disk: {filePath}", filePath);
        }

        // The storage endpoint is a Google Cloud Storage (GCS) pre-signed POST URL —
        // NOT the CMP API. Optimizely CMP DAM uses GCS as its underlying object store
        // (https://storage.googleapis.com/...), so the body must satisfy the GCS V4
        // signed POST policy. The contract Optimizely documents at
        // https://docs.developers.optimizely.com/content-marketing-platform/docs/upload-assets
        // is:
        //   - The payload must be multipart/form-data.
        //   - The field name for the file must be "file".
        //   - Meta fields must appear in the order received from /v3/upload-url.
        //   - The "file" field must be appended LAST.
        //   - Success is signalled by HTTP 204.
        //
        // Beyond the documented rules, GCS validates the signature against an exact
        // body shape. Two implementation details that look harmless but break the
        // signature in practice:
        //
        //   (a) The file part must be added as `form.Add(fileContent, "file")` — without
        //       a filename argument. Passing a filename causes .NET to emit
        //       `Content-Disposition: form-data; name="file"; filename="..."; filename*=utf-8''...`
        //       which changes the field's identity from GCS's perspective, producing a
        //       400 "Malformed multipart body".
        //   (b) The string parts must keep their default `Content-Type: text/plain;
        //       charset=utf-8` header. Stripping it changes the bytes that GCS hashes.
        //
        // We therefore mirror exactly what the working pre-refactor version did. If you
        // are tempted to tighten this further, run the integration test against a real
        // CMP sandbox first.
        var storageClient = _httpClientFactory.CreateClient(StorageHttpClientName);

        // MultipartFormDataContent disposes its child HttpContent instances when it
        // is itself disposed, but CA2000 can't prove that across method boundaries.
        // Track every child in a local list and dispose them explicitly so the
        // analyzer is satisfied AND we don't leak if construction throws midway.
        var ownedContent = new List<HttpContent>();
        MultipartFormDataContent? form = null;

        try
        {
            form = new MultipartFormDataContent();

            // 1. Meta fields in the exact order received. Default Content-Type on each
            //    StringContent is intentionally left alone.
            foreach (var (name, value) in uploadInfo.UploadMetaFields)
            {
                var part = new StringContent(value ?? string.Empty);
                ownedContent.Add(part);
                form.Add(part, name);
            }

            // 2. The file, last. Read into memory (mirrors the working original) — the
            //    GCS POST endpoint expects a complete request with a known Content-Length,
            //    and ByteArrayContent guarantees that. For very large assets, switch to
            //    CMP's separate multipart-uploads API rather than streaming through here.
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(bytes);
            ownedContent.Add(fileContent);

            // IMPORTANT: do NOT pass a filename here — see the block comment above.
            form.Add(fileContent, "file");

            using var response = await storageClient
                .PostAsync(uploadInfo.Url, form, cancellationToken)
                .ConfigureAwait(false);

            // The docs explicitly say "Upon success (204 HTTP response code)". Treat
            // any other status as a failure and surface the body so the operator can
            // see what GCS actually returned.
            if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                var body = await response.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Storage upload to '{uploadInfo.Url}' failed: " +
                    $"{(int)response.StatusCode} {response.ReasonPhrase}. " +
                    $"Storage response body: {body}");
            }

            _logger.LogDebug("Uploaded {File} to storage successfully (204).", filePath);
        }
        finally
        {
            form?.Dispose();
            foreach (var content in ownedContent)
            {
                content.Dispose();
            }
        }
    }

    public async Task<AssetCreateResponse> CreateAssetAsync(
        CreateAssetRequest request, CancellationToken cancellationToken = default)
    {
        var path = $"/{_options.ApiVersion}/assets";
        using var response = await _httpClient
            .PostAsJsonAsync(path, request, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var created = await response.Content
            .ReadFromJsonAsync<AssetCreateResponse>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Asset creation returned an empty body.");
        return created;
    }

    public async Task<AssetDetails> PatchAssetMetadataAsync(
        string assetId,
        string assetType,
        AssetMetadataPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var segment = assetType.ToLowerInvariant() switch
        {
            "image"    => "images",
            "video"    => "videos",
            "raw_file" => "raw-files",
            _          => "images",
        };
        var path = $"/{_options.ApiVersion}/{segment}/{Uri.EscapeDataString(assetId)}";

        using var message = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // The PATCH response body carries the updated asset, including its canonical
        // URL field. We capture it so callers don't need to make a second GET call.
        var details = await response.Content
            .ReadFromJsonAsync<AssetDetails>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return details ?? new AssetDetails { Id = assetId };
    }

    public async Task SetAssetFieldsAsync(
        string assetId,
        IReadOnlyList<AssetFieldValue> fieldValues,
        CancellationToken cancellationToken = default)
    {
        if (fieldValues.Count == 0)
        {
            _logger.LogDebug("No custom field values to set on asset {AssetId}; skipping PUT.", assetId);
            return;
        }

        var path = $"/{_options.ApiVersion}/assets/{Uri.EscapeDataString(assetId)}/fields";
        using var message = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(fieldValues, options: SerializerOptions),
        };
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"CMP DAM request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
    }
}
