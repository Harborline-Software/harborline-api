using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// Generic subsidiary-ledger account identity (ADR 0120 D1). One
/// <see cref="SubLedgerAccount"/> represents a single counterparty
/// relationship's detail under a GL control account within one legal entity.
///
/// <para>
/// <b>What this carries:</b> identity + the binding to a GL control account
/// + the counterparty (Party) + entity scope (via <see cref="ChartId"/>).
/// <b>What it does NOT carry:</b> a stored balance (position is derived by
/// <c>ISubLedgerReadModel.GetPositionAsync</c> — see PR-B projection assembly),
/// any open-item list, or any domain field (<c>LeaseId</c>, <c>UnitId</c>,
/// etc. — the D3 pack-boundary arch-test enforces this at build time).
/// </para>
///
/// <para>
/// <b>Position derivation (C-FIN-1):</b> the position of a sub-ledger account
/// is <c>Σ(open-item.Balance) − Σ(unapplied credits)</c>. Open-item
/// <c>Balance</c> already nets <c>DiscountAmount</c> + <c>WriteoffAmount</c>
/// via <c>AmountPaid</c> — do NOT derive from <c>Σ AmountApplied</c> or
/// discounted/short-paid items will show phantom residuals and the R1
/// reconciliation invariant will fail.
/// </para>
///
/// <para>
/// <b>R1 reconciliation invariant (per <see cref="Kind"/>):</b>
/// ∑(derived positions for all <see cref="SubLedgerAccount"/>s with
/// <see cref="ControlAccountId"/> = C in chart K) == GL balance of C in K.
/// AR sub-ledgers reconcile to an Asset-class control; AP to a Liability-class
/// control — never across kinds (C-FIN-4 guard; enforced by the arch-test).
/// </para>
///
/// <para>
/// <b>Lifecycle:</b> created when a relationship's first chargeable event
/// occurs; soft-closed (<see cref="IsActive"/>=false) when the relationship
/// ends and the balance reaches zero. Never hard-deleted while history exists
/// (per <c>project_mvp_entity_delete_semantics</c> — archive/soft-delete
/// posture, consistent with ADR 0108).
/// </para>
///
/// <para>
/// <b>CRDT envelope:</b> <see cref="CreatedBy"/>, <see cref="UpdatedBy"/>,
/// and <see cref="Version"/> align with the sibling <c>Payment</c> and
/// <c>Invoice</c> masters (C-ARCH-3). The signed-event envelope is deferred
/// to the durable mutation layer (ADR 0120 §6; not inline).
/// </para>
/// </summary>
public sealed record SubLedgerAccount : IMustHaveTenant
{
    /// <summary>Stable identifier.</summary>
    public required SubLedgerAccountId Id { get; init; }

    /// <summary>
    /// Tenant scope. Required — non-default per <see cref="IMustHaveTenant"/>.
    /// In-memory store uses composite <c>(TenantId, Id)</c> keying per the
    /// financial-cluster posture.
    /// </summary>
    public required TenantId TenantId { get; init; }

    /// <summary>
    /// The chart of accounts under which this sub-ledger account lives.
    /// <c>ChartId → LegalEntityId</c> gives per-entity subsidiary ledgers
    /// by construction (ADR 0104 — multi-entity falls out for free).
    /// </summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// The GL control account this subsidiary-ledger account rolls up to.
    /// Must be an <c>IsPostable</c>-hierarchy <see cref="GLAccount"/>:
    /// an Asset-class account for <see cref="SubLedgerKind.Receivable"/>,
    /// a Liability-class account for <see cref="SubLedgerKind.Payable"/>.
    /// The Kind↔control-account-type consistency guard is asserted by the
    /// D3 arch-test (<c>SubLedgerPackBoundaryArchitectureTests</c>).
    /// </summary>
    public required GLAccountId ControlAccountId { get; init; }

    /// <summary>
    /// Receivable (AR) or Payable (AP) — mirrors the <c>PaymentDirection</c>
    /// axis and the Invoice/Bill split.
    /// </summary>
    public required SubLedgerKind Kind { get; init; }

    /// <summary>
    /// The counterparty (customer for Receivable, vendor for Payable).
    /// Domain packs (e.g. PM) relate a lease/contract to this account via
    /// a pack-owned link record — never via a field on this master.
    /// </summary>
    public required PartyId PartyId { get; init; }

    /// <summary>
    /// Optional parent subsidiary account for finer-grained nesting (ADR 0120
    /// Open Q2 escape hatch). Present in the model; unused in v1. A party-level
    /// rollup is computed as a ∑-query over child accounts.
    /// </summary>
    public SubLedgerAccountId? ParentSubLedgerAccountId { get; init; }

    /// <summary>
    /// Optional human-readable label (e.g. <c>"AR — Acme Corp / Lease L-204"</c>).
    /// Display-only; not used for reconciliation or matching.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Optional external-system reference (e.g. ERPNext party/account ref)
    /// for import idempotency and migration matching.
    /// </summary>
    public string? ExternalRef { get; init; }

    /// <summary>
    /// Soft-close flag (ADR 0108 archive posture). Set to <c>false</c> when the
    /// relationship ends and the balance reaches zero. Never hard-deleted while
    /// history exists.
    /// </summary>
    public bool IsActive { get; init; } = true;

    // ── CRDT envelope — matches Payment.cs:105-110 / Invoice.cs:103-111 ──
    public required Instant CreatedAtUtc { get; init; }
    public PartyId? CreatedBy { get; init; }
    public Instant UpdatedAtUtc { get; init; }
    public PartyId? UpdatedBy { get; init; }
    public required long Version { get; init; }

    /// <summary>
    /// Validates that <paramref name="kind"/> is consistent with
    /// <paramref name="controlAccountType"/> per C-FIN-4 (ADR 0120 PR-C hardening).
    ///
    /// <para>
    /// <b>Rule:</b>
    /// <list type="bullet">
    ///   <item><see cref="SubLedgerKind.Receivable"/> must bind an
    ///     <see cref="GLAccountType.Asset"/> control account (AR control).</item>
    ///   <item><see cref="SubLedgerKind.Payable"/> must bind a
    ///     <see cref="GLAccountType.Liability"/> control account (AP control).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Callers that have access to the <c>GLAccountType</c> of the control account
    /// (e.g. at account-creation time after a GL lookup) MUST call this guard.
    /// A mis-bind would surface as an R1 mismatch in production rather than silently
    /// corrupting data — but the guard makes mis-binding impossible at the creation
    /// seam when the type is available.
    /// </para>
    ///
    /// <para>
    /// The PR-B SPOT-CHECK (flag #2) noted that the prior test only asserted
    /// Kind-filtered summation exclusion, not REJECTION. This method provides the
    /// rejection mechanism; use it in creation flows + test it directly.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="kind"/>↔<paramref name="controlAccountType"/> is inconsistent.
    /// </exception>
    public static void ValidateKindControlAccountConsistency(
        SubLedgerKind kind,
        GLAccountType controlAccountType)
    {
        var isValid = kind switch
        {
            SubLedgerKind.Receivable => controlAccountType == GLAccountType.Asset,
            SubLedgerKind.Payable    => controlAccountType == GLAccountType.Liability,
            _ => false,
        };

        if (!isValid)
        {
            throw new ArgumentException(
                $"Sub-ledger Kind '{kind}' is incompatible with control account type '{controlAccountType}'. " +
                $"Receivable sub-ledgers must bind an Asset control account; " +
                $"Payable sub-ledgers must bind a Liability control account. " +
                $"A mis-bind would corrupt the R1 reconciliation invariant (ADR 0120 C-FIN-4).");
        }
    }

    /// <summary>
    /// Construct a new active sub-ledger account. <see cref="IsActive"/> defaults
    /// to <c>true</c>; <see cref="Version"/> is set to 1.
    ///
    /// <para>
    /// <b>Prefer <see cref="CreateWithKindGuard"/> whenever the control account's
    /// <see cref="GLAccountType"/> is available at the call site.</b>
    /// <c>CreateWithKindGuard</c> rejects Kind↔control-account-type mis-binds at the
    /// creation seam and is the canonical path for all live mint operations (ADR 0120
    /// PR-D; PR-B SPOT-CHECK flag #2 close).
    /// </para>
    ///
    /// <para>
    /// <b>Why a hard invariant is not enforced on the record itself (FU-2 disposition):</b>
    /// <c>SubLedgerAccount</c> is a <c>sealed record</c> with <c>required init</c> members.
    /// Records support direct object-initializer construction (<c>new SubLedgerAccount { … }</c>)
    /// by design — this is required for EF Core materialisation, System.Text.Json deserialisation,
    /// <c>with</c>-expression clones, and test-fixture construction. Any guard placed in a
    /// <c>required init</c> property setter would fire on EF/JSON materialisation (which
    /// reads from storage without supplying a resolved <c>GLAccountType</c> — the FK is a
    /// <c>GLAccountId</c> string, not a typed Account entity). There is no Roslyn-enforced
    /// pattern for "factory-only" creation on a record without sealing all constructors, which
    /// would break record syntax entirely. The two-factory design (<c>Create</c> / <c>CreateWithKindGuard</c>)
    /// is the right pattern for this type: callers that own a live mint operation and
    /// know the GL account type use the guarded factory; callers that reconstruct from
    /// storage or structurally-pinned data (migration, deserialisation) use the unguarded one.
    /// A mis-bind via <c>Create</c> remains detectable at R1 reconciliation time (the cross-foot
    /// fires when positions don't match GL), so it is not a silent data-corruption defect.
    /// </para>
    ///
    /// <para>
    /// <b>All production mint callers as of ADR 0120 PR-D:</b>
    /// <list type="bullet">
    ///   <item><c>LeaseSubLedgerService.ActivateAsync</c> — uses <c>CreateWithKindGuard</c> (Asset, Receivable).</item>
    ///   <item><c>SubLedgerMigrationService</c> (invoice path) — kind structurally pinned to Receivable
    ///     from AR invoice source; <c>Create</c> is safe here (no GLAccountType available at migration time).</item>
    ///   <item><c>SubLedgerMigrationService</c> (bill path) — kind structurally pinned to Payable
    ///     from AP bill source; same reasoning.</item>
    /// </list>
    /// No other production caller mints a <c>SubLedgerAccount</c> without either the kind guard
    /// or a structurally-pinned kind derivation.
    /// </para>
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static SubLedgerAccount Create(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        GLAccountId controlAccountId,
        SubLedgerKind kind,
        PartyId partyId,
        Instant now,
        PartyId? createdBy = null,
        string? reference = null,
        string? externalRef = null,
        SubLedgerAccountId? parentSubLedgerAccountId = null)
        => new()
        {
            Id = SubLedgerAccountId.NewId(),
            TenantId = tenantId,
            ChartId = chartId,
            ControlAccountId = controlAccountId,
            Kind = kind,
            PartyId = partyId,
            ParentSubLedgerAccountId = parentSubLedgerAccountId,
            Reference = reference,
            ExternalRef = externalRef,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            UpdatedAtUtc = now,
            Version = 1,
        };

    /// <summary>
    /// Construct a new active sub-ledger account with Kind↔control-account-type validation
    /// (ADR 0120 PR-D — closes PR-B SPOT-CHECK flag #2). Throws
    /// <see cref="ArgumentException"/> when <paramref name="kind"/> is inconsistent with
    /// <paramref name="controlAccountType"/>:
    /// <list type="bullet">
    ///   <item><see cref="SubLedgerKind.Receivable"/> must bind an
    ///     <see cref="GLAccountType.Asset"/> control account.</item>
    ///   <item><see cref="SubLedgerKind.Payable"/> must bind a
    ///     <see cref="GLAccountType.Liability"/> control account.</item>
    /// </list>
    /// Use this overload at creation seams where the control account type is resolvable
    /// (e.g. lease activation where the caller has loaded the AR GLAccount record).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="kind"/>↔<paramref name="controlAccountType"/> is inconsistent.
    /// </exception>
    public static SubLedgerAccount CreateWithKindGuard(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        GLAccountId controlAccountId,
        GLAccountType controlAccountType,
        SubLedgerKind kind,
        PartyId partyId,
        Instant now,
        PartyId? createdBy = null,
        string? reference = null,
        string? externalRef = null,
        SubLedgerAccountId? parentSubLedgerAccountId = null)
    {
        ValidateKindControlAccountConsistency(kind, controlAccountType);
        return new SubLedgerAccount
        {
            Id = SubLedgerAccountId.NewId(),
            TenantId = tenantId,
            ChartId = chartId,
            ControlAccountId = controlAccountId,
            Kind = kind,
            PartyId = partyId,
            ParentSubLedgerAccountId = parentSubLedgerAccountId,
            Reference = reference,
            ExternalRef = externalRef,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }
}
