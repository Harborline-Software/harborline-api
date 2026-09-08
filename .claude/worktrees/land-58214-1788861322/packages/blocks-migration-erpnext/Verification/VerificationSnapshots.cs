using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Migration.Erpnext.Verification;

/// <summary>
/// Parsed shape of the optional CO-prepared <c>ar-aging-snapshot.json</c> (spec §4.6 step 2) —
/// the source-ERPNext AR aging position captured one-time on the export machine. Pass 6 diffs
/// Anchor's recomputed aging against this to prove the receivables position survived the import.
/// </summary>
/// <remarks>
/// The orchestrator (A7) is responsible for locating + JSON-parsing the file from the export root;
/// Pass 6 receives the already-parsed structure (or <see langword="null"/> when the optional file
/// is absent, in which case the AR aging check is skipped). Keeping the filesystem + parsing in the
/// orchestrator leaves Pass 6 pure and unit-testable without touching disk.
/// </remarks>
public sealed record ArAgingSnapshot(IReadOnlyList<PartyAgingSnapshotRow> Customers);

/// <summary>
/// Parsed shape of the optional CO-prepared <c>ap-aging-snapshot.json</c> (spec §4.6 step 3).
/// Symmetric to <see cref="ArAgingSnapshot"/> but scoped to vendors / payables.
/// </summary>
public sealed record ApAgingSnapshot(IReadOnlyList<PartyAgingSnapshotRow> Vendors);

/// <summary>
/// One party's expected aging buckets from a CO-prepared snapshot. Bucket layout mirrors
/// <c>Harborline.Api.Blocks.FinancialAr.Models.AgingSummary</c> so the diff is a straight bucket-by-bucket
/// comparison.
/// </summary>
/// <param name="PartyId">The customer (AR) or vendor (AP) the buckets belong to.</param>
/// <param name="Current">Expected balance not yet past due.</param>
/// <param name="Days0To30">Expected balance 1–30 days past due.</param>
/// <param name="Days31To60">Expected balance 31–60 days past due.</param>
/// <param name="Days61To90">Expected balance 61–90 days past due.</param>
/// <param name="Days90Plus">Expected balance 91+ days past due.</param>
public sealed record PartyAgingSnapshotRow(
    PartyId PartyId,
    decimal Current,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Days90Plus);

/// <summary>
/// Parsed shape of the optional CO-prepared <c>gl-balances-snapshot.json</c> (spec §4.6 step 4) —
/// per-account closing balances captured on the source ERPNext. Pass 6 diffs Anchor's recomputed
/// per-account journal-line sums against these.
/// </summary>
/// <remarks>
/// Accounts are keyed by their human-readable <c>Code</c> (e.g. <c>"4000"</c>), not by Anchor's
/// opaque <c>GLAccountId</c> — the code is the stable join key preserved across the Pass 1 chart
/// mapping, whereas the GLAccountId is minted Anchor-side and has no source counterpart. Balances
/// are <b>signed</b> (debit total minus credit total), matching
/// <c>IGeneralLedgerReadModel.GetAccountBalancesAsOfAsync</c>.
/// </remarks>
public sealed record GlBalancesSnapshot(IReadOnlyList<AccountBalanceSnapshotRow> Accounts);

/// <summary>One account's expected signed balance from a CO-prepared snapshot.</summary>
/// <param name="AccountCode">Human-readable account code (the cross-system join key).</param>
/// <param name="SignedBalance">Expected debit-minus-credit balance.</param>
public sealed record AccountBalanceSnapshotRow(string AccountCode, decimal SignedBalance);
