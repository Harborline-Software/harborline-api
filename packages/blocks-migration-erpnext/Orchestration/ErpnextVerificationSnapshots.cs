using Harborline.Api.Blocks.Migration.Erpnext.Verification;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The three optional CO-prepared verification snapshots Pass 6 diffs against
/// (spec §4.6 steps 2-4), already parsed into their typed shapes. Bundled into one
/// record so the run request carries a single verification-snapshot input rather
/// than three loose nullables.
/// </summary>
/// <remarks>
/// <para>
/// <b>The CLI host owns the disk I/O.</b> The host locates + JSON-parses
/// <c>ar-aging-snapshot.json</c> / <c>ap-aging-snapshot.json</c> /
/// <c>gl-balances-snapshot.json</c> from the export root and hands the orchestrator
/// the parsed objects (or <see cref="None"/> when none are present). This keeps the
/// orchestrator + Pass 6 pure and unit-testable without touching the filesystem —
/// the same disk-stays-in-the-host discipline the extractor follows.
/// </para>
/// <para>
/// Any individual snapshot may be <see langword="null"/>: a null snapshot tells
/// Pass 6 to SKIP that particular check (AR aging, AP aging, or per-account balance)
/// rather than fail it — an absent CO snapshot is a "not provided", never a mismatch.
/// </para>
/// </remarks>
/// <param name="ArAging">The parsed AR aging snapshot, or <see langword="null"/> to skip the AR aging check.</param>
/// <param name="ApAging">The parsed AP aging snapshot, or <see langword="null"/> to skip the AP aging check.</param>
/// <param name="GlBalances">The parsed per-account balance snapshot, or <see langword="null"/> to skip the per-account balance check.</param>
public sealed record ErpnextVerificationSnapshots(
    ArAgingSnapshot? ArAging,
    ApAgingSnapshot? ApAging,
    GlBalancesSnapshot? GlBalances)
{
    /// <summary>No CO snapshots supplied — Pass 6 runs the trial-balance + invoice-balance checks but skips all three snapshot-gated checks.</summary>
    public static ErpnextVerificationSnapshots None { get; } = new(null, null, null);
}
