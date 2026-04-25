using ImportAssetToOptiDam.Models.Dam;

namespace ImportAssetToOptiDam.Services.Dam;

/// <summary>
/// Abstraction over the Optimizely CMP DAM REST API. Hides transport, auth,
/// serialization, and URL composition from callers.
/// </summary>
public interface IOptimizelyDamClient
{
    Task<IReadOnlyList<DamField>> GetFieldsAsync(CancellationToken cancellationToken = default);

    Task<UploadUrlResponse> GetUploadUrlAsync(CancellationToken cancellationToken = default);

    Task UploadFileToStorageAsync(
        UploadUrlResponse uploadInfo,
        string filePath,
        CancellationToken cancellationToken = default);

    Task<AssetCreateResponse> CreateAssetAsync(
        CreateAssetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches metadata on an asset and returns the server's response, which
    /// includes the asset's canonical <c>url</c> field. Per Optimizely's API,
    /// that single URL is either public (no token) or private (signed,
    /// short-lived) depending on the asset's <c>is_public</c> setting.
    /// </summary>
    Task<AssetDetails> PatchAssetMetadataAsync(
        string assetId,
        string assetType,
        AssetMetadataPatchRequest request,
        CancellationToken cancellationToken = default);

    Task SetAssetFieldsAsync(
        string assetId,
        IReadOnlyList<AssetFieldValue> fieldValues,
        CancellationToken cancellationToken = default);
}
