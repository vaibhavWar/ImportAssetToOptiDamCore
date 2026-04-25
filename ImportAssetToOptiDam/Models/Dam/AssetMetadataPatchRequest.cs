using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Dam;

/// <summary>
/// PATCH body for /v3/{assetType}/{id}. Only non-null properties are serialized so
/// partial updates stay idempotent and we don't accidentally overwrite existing values.
/// </summary>
public sealed record AssetMetadataPatchRequest
{
    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("alt_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AltText { get; init; }

    [JsonPropertyName("attribution_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttributionText { get; init; }

    [JsonPropertyName("tags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("is_public")]
    public bool IsPublic { get; init; } = true;

    [JsonPropertyName("is_archived")]
    public bool IsArchived { get; init; }
}

/// <summary>
/// PUT body element for /v3/assets/{id}/fields — one per custom field.
/// </summary>
public sealed record AssetFieldValue(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("values")] IReadOnlyList<object> Values);

/// <summary>
/// POST body for creating an asset after direct upload.
/// </summary>
public sealed record CreateAssetRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("folder_id")] string FolderId);
