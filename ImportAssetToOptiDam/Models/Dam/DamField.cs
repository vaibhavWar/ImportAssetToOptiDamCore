using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Dam;

public sealed record DamFieldChoice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string? Color);

public sealed record DamField(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("helper_text")] string? HelperText,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("is_multi_select")] bool IsMultiSelect,
    [property: JsonPropertyName("choices")] IReadOnlyList<DamFieldChoice>? Choices,
    [property: JsonPropertyName("has_thousand_separator")] bool? HasThousandSeparator,
    [property: JsonPropertyName("decimal_places")] int? DecimalPlaces);

public sealed record DamFieldsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<DamField> Data);

public sealed record DamFolder(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parent_folder_id")] string? ParentFolderId,
    [property: JsonPropertyName("path")] string? Path);

public sealed record DamFoldersResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<DamFolder> Data);
