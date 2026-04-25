using Microsoft.Extensions.Options;

namespace ImportAssetToOptiDam.Configuration;

/// <summary>
/// Validates that the credential fields on <see cref="OptimizelyCmpOptions"/> have been
/// populated with real values rather than the placeholder strings that ship in
/// <c>appsettings.json</c>. Runs in addition to DataAnnotations so operators get a clear,
/// specific error when they forget to replace the defaults or when a whitespace-only
/// value sneaks through from a copy-paste.
/// </summary>
public sealed class OptimizelyCmpOptionsValidator : IValidateOptions<OptimizelyCmpOptions>
{
    /// <summary>
    /// The placeholder values that live in the committed <c>appsettings.json</c>. Any
    /// match here is treated as "not yet configured" rather than a real credential.
    /// </summary>
    internal static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal)
    {
        "REPLACE_WITH_CLIENT_ID",
        "REPLACE_WITH_CLIENT_SECRET",
    };

    public ValidateOptionsResult Validate(string? name, OptimizelyCmpOptions options)
    {
        var failures = new List<string>();

        ValidateCredential(nameof(options.ClientId), options.ClientId, failures);
        ValidateCredential(nameof(options.ClientSecret), options.ClientSecret, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateCredential(string fieldName, string? value, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"OptimizelyCmp:{fieldName} is not configured. Set it in appsettings.json, " +
                         $"as an environment variable (OptimizelyCmp__{fieldName}), or via User Secrets.");
            return;
        }

        if (Placeholders.Contains(value))
        {
            failures.Add($"OptimizelyCmp:{fieldName} still contains the placeholder value from " +
                         $"appsettings.json. Replace it with a real credential before running the import.");
            return;
        }

        if (value != value.Trim())
        {
            failures.Add($"OptimizelyCmp:{fieldName} has leading or trailing whitespace, which will " +
                         $"cause the token endpoint to return 401. Re-copy the value without padding.");
        }
    }
}
