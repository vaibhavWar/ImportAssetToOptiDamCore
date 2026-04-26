using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Dam;

/// <summary>
/// Response from <c>GET /v3/asset-urls/{asset_id}</c>. CMP exposes several URL flavours
/// on this endpoint and the exact set varies by asset type and tenant configuration.
/// We capture the documented common fields plus a fallback bag of every other URL-shaped
/// property so the report stage can cope with schema drift without a code change.
/// </summary>
public sealed record AssetUrls
{
    /// <summary>Publicly-shareable URL — present once a public link has been generated.</summary>
    [JsonPropertyName("public_url")]
    public string? PublicUrl { get; init; }

    /// <summary>Internal CMP URL that requires an authenticated session.</summary>
    [JsonPropertyName("private_url")]
    public string? PrivateUrl { get; init; }

    /// <summary>Direct download URL for the source bytes (auth-required).</summary>
    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; init; }

    /// <summary>CMP's hosted preview URL.</summary>
    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; init; }

    /// <summary>
    /// Any additional URL-bearing fields the server returned that aren't modelled above.
    /// Keeps schema drift from breaking the importer.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; init; }

    /// <summary>
    /// Best-effort resolution of "the public URL" — falls through public_url → preview_url
    /// → any extension property whose name contains "public".
    /// </summary>
    public string? ResolvePublicUrl()
    {
        if (!string.IsNullOrWhiteSpace(PublicUrl))  return PublicUrl;
        if (!string.IsNullOrWhiteSpace(PreviewUrl)) return PreviewUrl;
        return FindExtensionUrl(name => name.Contains("public", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Best-effort resolution of "the private URL" — falls through private_url →
    /// download_url → any extension property whose name contains "private" or "download".
    /// </summary>
    public string? ResolvePrivateUrl()
    {
        if (!string.IsNullOrWhiteSpace(PrivateUrl))  return PrivateUrl;
        if (!string.IsNullOrWhiteSpace(DownloadUrl)) return DownloadUrl;
        return FindExtensionUrl(name =>
            name.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("download", StringComparison.OrdinalIgnoreCase));
    }

    private string? FindExtensionUrl(Func<string, bool> namePredicate)
    {
        if (AdditionalProperties is null) return null;
        foreach (var (name, element) in AdditionalProperties)
        {
            if (!namePredicate(name)) continue;
            if (element.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
