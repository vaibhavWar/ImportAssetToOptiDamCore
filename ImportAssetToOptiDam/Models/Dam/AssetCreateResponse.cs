using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Dam;

/// <summary>
/// Response returned by POST /v3/assets after we register the uploaded bytes.
/// </summary>
public sealed record AssetCreateResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("mime_type")] string? MimeType);
