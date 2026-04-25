using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Authentication;

/// <summary>
/// JSON body sent to the Optimizely CMP token endpoint.
/// </summary>
/// <remarks>
/// Optimizely CMP expects a JSON-encoded payload at the token endpoint, which
/// is a vendor-specific deviation from RFC 6749 (which calls for
/// <c>application/x-www-form-urlencoded</c>). Property names match the
/// snake_case keys in the documented contract.
/// </remarks>
public sealed record TokenRequestBody
{
    [JsonPropertyName("grant_type")]
    public string GrantType { get; init; } = "client_credentials";

    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = default!;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; init; } = default!;
}
