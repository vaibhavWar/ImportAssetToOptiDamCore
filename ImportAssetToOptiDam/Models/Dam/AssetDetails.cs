using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Dam;

/// <summary>
/// Subset of fields from the asset response (returned by PATCH /v3/images/{id},
/// GET /v3/images/{id}, etc.) that we actually consume. The full schema is large
/// and tenant-dependent; <see cref="AdditionalProperties"/> retains anything the
/// server returns that we haven't modelled.
/// </summary>
/// <remarks>
/// Per Optimizely docs: <em>"url – This is the URL to access the asset. If the
/// asset is private, the URL includes a token that expires after a short time."</em>
/// In other words there is one canonical URL per asset, and its public-vs-private
/// flavour is determined by the asset's <c>is_public</c> setting at the time of
/// retrieval — not by separate fields.
/// </remarks>
public sealed record AssetDetails
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("is_public")]
    public bool? IsPublic { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; init; }
}
