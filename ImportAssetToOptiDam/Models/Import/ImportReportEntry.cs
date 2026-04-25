namespace ImportAssetToOptiDam.Models.Import;

/// <summary>
/// One successfully-imported asset, captured for the post-import Excel report.
/// </summary>
public sealed record ImportReportEntry(
    string NewFileName,
    string? PublicDamUrl,
    string? PrivateDamUrl);
