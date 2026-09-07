using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Default <see cref="IJournalPostingService"/> implementing the Stage 02
/// §6.1 six-phase algorithm:
///
///   1. Preconditions — must be <see cref="JournalEntryStatus.Draft"/>
///      with ≥2 lines.
///   1.5 Posting idempotency (ADR 0122 §D2) — when the entry carries a
///      non-null <see cref="JournalEntry.SourceReference"/>, a re-driven
///      source event (same <c>SourceReference</c>) returns the already-posted
///      entry instead of posting a second JE. Keyed on
///      <see cref="JournalEntry.SourceReference"/> ALONE (NOT a
///      <c>(SourceKind, SourceReference)</c> composite — <c>SourceKind</c> is
///      inconsistently populated and stays a reporting dimension only). Manual
///      JEs (<c>SourceReference == null</c>) are NOT deduped. The dedupe state
///      is the persisted JE record on the recoverable store (the
///      <see cref="IJournalStore.FindBySourceReferenceAsync"/> pre-write
///      lookup + the unique index on the <c>SourceReference</c> column), never
///      a seed-keyed/in-memory KV index (SC-4-critical; ADR 0122 §D2).
///   2. Balance — defense-in-depth re-check of Σ debit == Σ credit
///      (the <see cref="JournalEntry"/> constructor also enforces).
///   3. Account validity — each line's account exists, belongs to the
///      entry's chart (when set), and is postable.
///   4. Period gating — fiscal period for the entry date must exist and
///      be Open (or SoftClosed with the explicit override permission).
///   5. Atomic commit — promote to Posted + persist via
///      <see cref="IJournalStore.SaveAtomicAsync"/>; the store rolls
///      back any partial writes on exception. The unique index on
///      <c>SourceReference</c> is the durable backstop for a race that
///      slips past phase 1.5.
///   6. Result — <see cref="PostResult"/> with the promoted entry on
///      success; structured <see cref="PostError"/> on validation
///      failure (no exception thrown).
/// </summary>
public sealed class JournalPostingService : IJournalPostingService
{
    private readonly IAccountResolver _accounts;
    private readonly IPeriodResolver _periods;
    private readonly IJournalStore _store;
    private readonly AuthorizationGate _gate;

    public JournalPostingService(
        IAccountResolver accounts,
        IPeriodResolver periods,
        IJournalStore store,
        AuthorizationGate gate)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _periods  = periods  ?? throw new ArgumentNullException(nameof(periods));
        _store    = store    ?? throw new ArgumentNullException(nameof(store));
        _gate     = gate     ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async Task<PostResult> PostAsync(
        JournalEntry entry,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TenantId != authority.Tenant)
            throw new ArgumentException("The journal entry tenant does not match the write authority.", nameof(entry));

        var postDecision = await _gate.DecideAsync(
            authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
                "journal-entry",
                entry.Id.Value),
            cancellationToken).ConfigureAwait(false);
        postDecision.RequireAllowed();

        return await PostCoreAsync(
            entry,
            authority.Principal,
            authority.Tenant,
            authority.At,
            postDecision,
            persist: true,
            cancellationToken,
            authorizeSoftClose: (snapshot, ct) => AuthorizeSoftCloseAsync(
                authority.Principal, authority.Tenant, authority.At, snapshot, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Verification-only posting path for a synchronous workflow reaction. The route decided the exact
    /// journal entry before the workflow writer opened; this overload verifies that carried evidence and
    /// prepares the posted row without asking the gate a second time. The caller-provided stage callback
    /// enlists the row in the workflow transaction.
    /// </summary>
    internal Task<PostResult> PostAsync(
        JournalEntry entry,
        AuthorizationDecision carriedDecision,
        ActorId expectedPrincipal,
        DateTimeOffset expectedAt,
        Func<JournalEntry, CancellationToken, Task> stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(carriedDecision);
        ArgumentNullException.ThrowIfNull(stage);
        carriedDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
            entry.TenantId,
            "journal-entry",
            entry.Id.Value);
        if (carriedDecision.Request.Principal != expectedPrincipal ||
            carriedDecision.Request.At != expectedAt ||
            carriedDecision.DecidedAt != expectedAt ||
            entry.CreatedAtUtc.Value != expectedAt)
        {
            throw new ArgumentException(
                "The carried ledger decision principal or instant does not match the originating act and journal row.",
                nameof(carriedDecision));
        }
        return PostCoreAsync(
            entry,
            carriedDecision.Request.Principal,
            carriedDecision.Request.Tenant,
            carriedDecision.Request.At,
            carriedDecision,
            persist: false,
            cancellationToken,
            stage,
            authorizeSoftClose: null);
    }

    private async Task<PostResult> PostCoreAsync(
        JournalEntry entry,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationDecision postDecision,
        bool persist,
        CancellationToken cancellationToken,
        Func<JournalEntry, CancellationToken, Task>? stage = null,
        Func<IPeriodResolver.PeriodSnapshot, CancellationToken, Task<bool>>? authorizeSoftClose = null)
    {
        // The public boundary decided this act; the workflow boundary verified its carried decision.
        // Keeping that evidence explicit prevents the carried path from manufacturing a write context.
        postDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
            tenant,
            "journal-entry",
            entry.Id.Value);

        // Phase 1 — preconditions.
        if (entry.Status != JournalEntryStatus.Draft)
            return new PostResult(null, PostError.NotADraft, $"status={entry.Status}");
        if (entry.Lines.Count < 2)
            return new PostResult(null, PostError.TooFewLines, $"lines={entry.Lines.Count}");

        // Phase 1.5 — posting idempotency (ADR 0122 §D2). A subledger that re-drives
        // the same source event (same SourceReference) must NOT double-post. Keyed on
        // SourceReference ALONE — null (manual JEs) is a no-op (no dedupe). The lookup
        // reads the recoverable journal store; the unique index on the persisted
        // SourceReference column is the durable backstop (SC-4-critical placement).
        if (entry.SourceReference is { Length: > 0 } sourceReference)
        {
            var existing = await _store
                .FindBySourceReferenceAsync(entry.TenantId, sourceReference, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                // Idempotent re-drive: return the already-posted entry, do not post again.
                return new PostResult(existing, PostError.None, $"idempotent: source-reference={sourceReference}");
            }
        }

        // Phase 2 — balance (defense-in-depth; ctor enforces too).
        // Use decimal arithmetic — exact for currency amounts (Stage 02
        // §7 notes integer-minor-units is the schema canonical, but the
        // C# impl preserves decimal for back-compat; .NET decimal is
        // base-10 so no float roundoff).
        decimal debitSum  = 0m;
        decimal creditSum = 0m;
        foreach (var line in entry.Lines)
        {
            debitSum  += line.Debit;
            creditSum += line.Credit;
        }
        if (debitSum != creditSum)
        {
            return new PostResult(null, PostError.Imbalanced,
                $"debits={debitSum:F2}, credits={creditSum:F2}");
        }

        // Phase 3 — account validity.
        foreach (var line in entry.Lines)
        {
            var acct = await _accounts.GetAsync(line.AccountId, cancellationToken).ConfigureAwait(false);
            if (acct is null)
                return new PostResult(null, PostError.UnknownAccount, line.AccountId.Value);
            if (entry.ChartId is { } entryChart && acct.ChartId is { } acctChart && !acctChart.Equals(entryChart))
                return new PostResult(null, PostError.WrongChart,
                    $"account.ChartId={acctChart}, entry.ChartId={entryChart}");
            if (!acct.IsPostable)
                return new PostResult(null, PostError.AccountNotPostable, line.AccountId.Value);
        }

        // Phase 4 — period gating (only when the entry has a chart;
        // unscoped entries skip this).
        if (entry.ChartId is { } chartId)
        {
            var period = await _periods.ResolveAsync(chartId, entry.EntryDate, cancellationToken)
                .ConfigureAwait(false);
            if (period is not { } snapshot)
                return new PostResult(null, PostError.NoPeriodForDate,
                    $"chartId={chartId}, date={entry.EntryDate}");
            if (snapshot.Status == IPeriodResolver.Status.Locked)
                return new PostResult(null, PostError.PeriodLocked, $"periodId={snapshot.PeriodId}");
            if (snapshot.Status == IPeriodResolver.Status.SoftClosed)
            {
                if (authorizeSoftClose is null)
                    return new PostResult(null, PostError.PeriodSoftClosed,
                        $"periodId={snapshot.PeriodId}; a carried ledger:post decision cannot confer period override");
                if (!await authorizeSoftClose(snapshot, cancellationToken).ConfigureAwait(false))
                    return new PostResult(null, PostError.PeriodSoftClosed,
                        $"periodId={snapshot.PeriodId}, userId={principal.Value}");
            }
        }

        // Phase 5 — atomic commit.
        var posted = entry with
        {
            Status = JournalEntryStatus.Posted,
            PostedAtUtc = new Instant(at),
        };
        if (persist)
            await _store.SaveAtomicAsync(posted.TenantId, posted, postDecision, cancellationToken)
                .ConfigureAwait(false);
        else
            await stage!(posted, cancellationToken).ConfigureAwait(false);

        // Phase 6 — result.
        return new PostResult(posted, PostError.None, null);
    }

    private async Task<bool> AuthorizeSoftCloseAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        IPeriodResolver.PeriodSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var overrideDecision = await _gate.DecideAsync(
            Request(
                principal,
                tenant,
                at,
                AuthorizationOperation.Parse(Permission.FinancialPeriodOverrideSoftClose),
                "financial-period",
                snapshot.PeriodId.ToString()),
            cancellationToken).ConfigureAwait(false);
        return overrideDecision.Verdict is AuthorizationVerdict.Allowed;
    }

    private static AuthorizationGateRequest Request(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationOperation operation,
        string recordKind,
        string recordId)
    {
        if (recordId.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException("A record id cannot contain a scope separator.", nameof(recordId));
        var scope = ScopeExpression.Parse($"/records/{recordId}");
        return new AuthorizationGateRequest(
            new PermissionAtom(operation, scope),
            principal,
            tenant,
            new AuthorizationTarget(recordKind, recordId, scope),
            at);
    }
}
