namespace ImportAssetToOptiDam.Services.Authentication;

/// <summary>
/// Provides bearer access tokens for Optimizely CMP. Implementations are expected to
/// cache a token until shortly before it expires, then transparently refresh.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Returns a valid bearer access token. Refreshes if the cached token is
    /// missing or within the configured skew window of expiry.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
