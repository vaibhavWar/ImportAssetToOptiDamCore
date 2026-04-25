using System.Net.Http.Headers;
using System.Net.Http.Json;
using ImportAssetToOptiDam.Configuration;
using ImportAssetToOptiDam.Models.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImportAssetToOptiDam.Services.Authentication;

/// <summary>
/// Thread-safe token provider that caches the access token until just before
/// it expires (minus a configurable skew), then refreshes via the OAuth2
/// client-credentials flow.
/// </summary>
public sealed class CachingTokenProvider : ITokenProvider
{
    public const string HttpClientName = nameof(CachingTokenProvider);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OptimizelyCmpOptions _options;
    private readonly ILogger<CachingTokenProvider> _logger;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAtUtc = DateTimeOffset.MinValue;

    public CachingTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<OptimizelyCmpOptions> options,
        ILogger<CachingTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsTokenStillFresh())
        {
            return _cachedToken!;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-checked: a concurrent caller may have refreshed while we waited.
            if (IsTokenStillFresh())
            {
                return _cachedToken!;
            }

            var token = await RequestNewTokenAsync(cancellationToken).ConfigureAwait(false);
            _cachedToken = token.AccessToken;
            _cachedTokenExpiresAtUtc = DateTimeOffset.UtcNow
                .AddSeconds(token.ExpiresInSeconds - _options.TokenRefreshSkewSeconds);

            _logger.LogInformation(
                "Obtained new CMP access token; expires at {ExpiresAt:O} (refresh skew {Skew}s).",
                _cachedTokenExpiresAtUtc, _options.TokenRefreshSkewSeconds);

            return _cachedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsTokenStillFresh() =>
        _cachedToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiresAtUtc;

    private async Task<OAuthTokenResponse> RequestNewTokenAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Optimizely CMP expects a JSON body here. This deviates from the standard
        // OAuth2 RFC 6749 convention (which is form-encoded), so we serialize the
        // request payload explicitly rather than reaching for FormUrlEncodedContent.
        var payload = new TokenRequestBody
        {
            GrantType    = "client_credentials",
            ClientId     = _options.ClientId,
            ClientSecret = _options.ClientSecret,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Read the body so the operator can see the actual error from the IdP
            // (typically "invalid_client", "invalid_grant", etc.) rather than
            // an opaque "Response status code does not indicate success: 401".
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    " The token endpoint rejected the credentials. Verify that " +
                    "OptimizelyCmp:ClientId and OptimizelyCmp:ClientSecret are set " +
                    "correctly (User Secrets or env vars), have no leading/trailing " +
                    "whitespace, and belong to the environment targeted by " +
                    $"'{_options.TokenUrl}'.",
                System.Net.HttpStatusCode.BadRequest =>
                    " The token endpoint rejected the request shape. Check that " +
                    "TokenUrl points at the OAuth2 token endpoint and not a different " +
                    "API path.",
                _ => string.Empty,
            };

            _logger.LogError(
                "Token request to {TokenUrl} failed with {StatusCode}. Response body: {Body}",
                _options.TokenUrl, (int)response.StatusCode, body);

            throw new HttpRequestException(
                $"Token request failed: {(int)response.StatusCode} {response.ReasonPhrase}.{hint} " +
                $"Response body: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var token = await response.Content
            .ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Token endpoint returned an empty body.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Token endpoint returned no access_token.");
        }
        return token;
    }
}
