namespace Harborline.Api.Blocks.Migration.Erpnext.Verification;

/// <summary>
/// Tuning knobs for <see cref="ErpnextVerificationPass"/> (importer Pass 6, spec §4.6).
/// </summary>
/// <remarks>
/// The thresholds the spec pins ($0 trial-balance tolerance, $0.01 per-customer/per-account
/// aging + balance tolerance) are NOT configurable — they are correctness invariants, not
/// preferences. The only operator-facing escape is <see cref="AllowAgingDrift"/>, which maps to
/// the <c>--allow-aging-drift</c> CLI flag (spec §8.1): when set, an AR/AP aging diff that exceeds
/// the $0.01 threshold is downgraded from a hard halt to a warning in the report, letting the CO
/// accept a known source-side discrepancy rather than blocking the migration.
/// </remarks>
public sealed record VerificationOptions
{
    /// <summary>
    /// When <see langword="true"/>, AR/AP aging diffs over the $0.01 threshold are recorded as
    /// warnings instead of producing <see cref="VerificationOutcome.AgingReconciliationFailed"/>.
    /// Maps to the <c>--allow-aging-drift</c> CLI flag. Defaults to <see langword="false"/>.
    /// </summary>
    public bool AllowAgingDrift { get; init; }

    /// <summary>Default options: strict (no aging drift permitted).</summary>
    public static VerificationOptions Default { get; } = new();
}
