using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Models.Authentication;

/// <summary>
/// OAuth2 client-credentials token response from Optimizely CMP.
/// </summary>
public sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = default!;

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";
}
