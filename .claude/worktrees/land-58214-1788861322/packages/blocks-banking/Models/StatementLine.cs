using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A single normalized statement line — the unit produced by both file-import
/// parsers (CSV/OFX/QIF/CAMT.053) and the live <see cref="Feed.IBankFeedProvider"/> seam.
/// Per ADR 0112 Part 1 §2 — statement-line entity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Idempotency / dedup model (ADR 0112 fin-acct N1):</strong>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Feed lines</strong> (<see cref="ProviderTxnId"/> is non-null): dedupe on
///     <c>(AccountId, ProviderTxnId)</c>. A duplicate import is a no-op.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>File lines</strong> (<see cref="ProviderTxnId"/> is null): dedupe on a
///     content hash of <c>(AccountId, PostedAt, Amount, Description)</c>
///     <strong>scoped to <see cref="ImportSourceRef.BatchId"/> +
///     <see cref="ImportSourceRef.OrdinalWithinBatch"/></strong>.
///     This preserves two genuinely-identical lines within one statement
///     (e.g. two $20 ATM withdrawals) while deduping re-imports of the same file.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Match cardinality:</strong> matches are NOT a single ref on this record.
/// Use <see cref="MatchLink"/> (many-to-many) to represent split and aggregate cases
/// (ADR 0112 fin-acct C3).
/// </para>
/// <para>
/// <strong>Append-and-correct semantics:</strong> statement lines are never deleted.
/// A mis-imported line is <see cref="ReconciliationState.Excluded"/> via state
/// transition with provenance, not removed. See ADR 0112 §Delete semantics.
/// </para>
/// <para>
/// <strong>Pending → posted (feeds only in v1):</strong> for feed lines a pending
/// line reconciles to its posted form via <see cref="ProviderTxnId"/>. For file
/// imports (no provider id) v1 falls back to content-hash dedup (ADR 0112 fin-acct N2).
/// </para>
/// <para>
/// <strong>ProviderTxnId:</strong> provider-stable transaction id; null for file imports.
/// </para>
/// <para>
/// <strong>Pending:</strong> true while the transaction has not cleared the bank (feed-only in v1).
/// </para>
/// <para>
/// <strong>RawProviderBlob:</strong> opaque raw provider payload for re-derivation / debug (feeds).
/// ADR 0112 sec-eng C2: treated as a sealed/redacted-on-read field inside the
/// E2E-encrypted local-store envelope. null for file imports; provider blob stored for feeds
/// (not surfaced in read model without explicit raw-export opt-in).
/// </para>
/// </remarks>
public sealed record StatementLine(
    StatementLineId Id,
    TenantId TenantId,
    BankAccountId AccountId,
    string? ProviderTxnId,
    Instant PostedAt,
    decimal Amount,
    CurrencyCode Currency,
    string Description,
    bool Pending,
    ReconciliationState State,
    ImportSourceRef Source,
    string? RawProviderBlob,
    Instant CreatedAtUtc) : IMustHaveTenant;
