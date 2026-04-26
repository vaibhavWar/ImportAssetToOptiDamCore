using System.ComponentModel.DataAnnotations;

namespace ImportAssetToOptiDam.Configuration;

/// <summary>
/// Strongly-typed configuration for the Optimizely CMP / DAM integration.
/// Bound to the "OptimizelyCmp" section and validated on application start.
/// </summary>
public sealed class OptimizelyCmpOptions
{
    public const string SectionName = "OptimizelyCmp";

    [Required, Url]
    public string BaseUrl { get; init; } = default!;

    [Required, Url]
    public string TokenUrl { get; init; } = default!;

    [Required]
    public string ApiVersion { get; init; } = "v3";

    /// <summary>
    /// Optimizely CMP Client ID. Populated from <c>appsettings.json</c> by default;
    /// can be overridden with the <c>OptimizelyCmp__ClientId</c> environment variable
    /// or with User Secrets. Validated by <see cref="OptimizelyCmpOptionsValidator"/>.
    /// </summary>
    public string ClientId { get; init; } = default!;

    /// <summary>
    /// Optimizely CMP Client Secret. See <see cref="ClientId"/> for sources and overrides.
    /// </summary>
    public string ClientSecret { get; init; } = default!;

    /// <summary>
    /// Safety margin, in seconds, subtracted from the token's advertised lifetime
    /// so we refresh just before it actually expires.
    /// </summary>
    [Range(0, 600)]
    public int TokenRefreshSkewSeconds { get; init; } = 60;

    [Range(1, 500)]
    public int FieldsPageSize { get; init; } = 100;

    [Range(1, 100)]
    public int FoldersPageSize { get; init; } = 100;
}
