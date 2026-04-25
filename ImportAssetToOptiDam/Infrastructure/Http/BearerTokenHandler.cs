using System.Net.Http.Headers;
using ImportAssetToOptiDam.Services.Authentication;

namespace ImportAssetToOptiDam.Infrastructure.Http;

/// <summary>
/// DelegatingHandler that attaches a Bearer access token obtained from
/// <see cref="ITokenProvider"/> to every outgoing request. Registered on the
/// typed CMP client only (NOT on the token client, to avoid a cycle).
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    public BearerTokenHandler(ITokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
