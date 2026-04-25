using System.Text.Json.Serialization;
using ImportAssetToOptiDam.Infrastructure.Json;

namespace ImportAssetToOptiDam.Models.Dam;

/// <summary>
/// Response from <c>GET /v3/upload-url</c>: a pre-signed AWS POST endpoint plus the
/// meta-fields that must accompany the upload. Field order from the JSON body is
/// preserved because AWS pre-signed POST requires the policy fields in their
/// documented order, with the file appended last.
/// </summary>
public sealed record UploadUrlResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("upload_meta_fields"),
               JsonConverter(typeof(OrderedStringMapConverter))]
    IReadOnlyList<KeyValuePair<string, string>> UploadMetaFields)
{
    /// <summary>
    /// The storage key returned by CMP inside <c>upload_meta_fields</c>. Required
    /// later when registering the uploaded asset via <c>POST /v3/assets</c>.
    /// </summary>
    public string Key
    {
        get
        {
            foreach (var (name, value) in UploadMetaFields)
            {
                if (string.Equals(name, "key", StringComparison.Ordinal))
                {
                    return value;
                }
            }
            throw new InvalidOperationException("upload_meta_fields did not contain a 'key'.");
        }
    }
}
