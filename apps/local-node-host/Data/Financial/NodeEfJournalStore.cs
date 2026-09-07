using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IJournalStore"/> for the embedded local node
/// (Cohort D — the financial-ledger node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns journal-entry data.</b> Before Cohort D the node had the
/// <c>journal_entries</c> SCHEMA (contributed by <c>FinancialLedgerEntityModule</c> into
/// <see cref="LocalNodeDbContext"/>) but NO <see cref="IJournalStore"/> registration and no
/// JE writes — so its table was empty and JE reads still depended on the Bridge. This store
/// gives the node a write/read surface over the SAME SQLCipher financial store the payments
/// read-plane already projects over (the C1-durable source of truth), so a single-device
/// install no longer needs signal-bridge on the JE critical path.
/// </para>
/// <para>
/// <b>Mirrors the Bridge <c>EfJournalStore</c> contract</b> (signal-bridge
/// the earlier platform's <c>Bridge.Data.Financial.EfJournalStore</c>), with two differences only:
/// the backing context is <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher) rather than
/// the Bridge's Npgsql context, and the node has no ambient per-request query filter, so
/// <see cref="Snapshot"/>'s explicit <c>WHERE TenantId = @t</c> IS the tenant boundary
/// (defence-in-depth per ADR 0092). <see cref="JournalEntry.Lines"/> is a JSONB-converted
/// column on the <c>journal_entries</c> table — NOT a separate line table — so a snapshot is a
/// single <c>SELECT</c> with no <c>Include</c>, returning fully-hydrated balanced entries.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> The interface exposes a synchronous <see cref="Snapshot"/>; the EF
/// implementation uses a short-lived context from the injected factory (the ambient scoped
/// context may already be disposed at read time), mirroring the Bridge implementation's
/// defensive-copy posture.
/// </para>
/// <para>
/// <b>Tenant guard (ADR 0092 §A3).</b> <see cref="SaveAtomicAsync"/> asserts
/// <c>entry.TenantId == tenantId</c> at the boundary and throws
/// <see cref="System.ArgumentException"/> on mismatch — a caller bug. The node routes pass the
/// active-team-derived tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>;
/// ADR 0032 identity layer), not a fixed <c>"local"</c> sentinel. <see cref="Snapshot"/> filters by
/// tenant explicitly — the per-org isolation predicate; switching the active org switches the entries.
/// </para>
/// <para>
/// <b>Declared coordination.</b> The journal-post operation names the home-epoch, audit,
/// recurring-invoice, and issued-invoice invariants. <see cref="WriteEnlistmentRegistry"/> verifies that
/// every one has a registered <see cref="IWriteEnlistment"/> before any row is staged; a missing adapter
/// refuses the save. Registered adapters explicitly return <see cref="WriteEnlistmentOutcome.NotApplicable"/>
/// when their ambient scope does not match this write. The registry runs the home-epoch fence first, and
/// an active fence scope keeps the existing <c>BEGIN IMMEDIATE</c> transaction around the fence read and
/// single save.
/// </para>
/// </remarks>
public sealed class NodeEfJournalStore : IJournalStore
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly WriteEnlistmentRegistry _enlistmentRegistry;

    /// <summary>Constructs the declared journal chokepoint over the registered Platform adapters.</summary>
    public NodeEfJournalStore(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IEnumerable<IWriteEnlistment> enlistments)
    {
        _contextFactory = contextFactory
            ?? throw new System.ArgumentNullException(nameof(contextFactory));
        ArgumentNullException.ThrowIfNull(enlistments);
        _enlistmentRegistry = new WriteEnlistmentRegistry(enlistments, NodeWriteInvariants.HomeEpoch);
    }

    /// <inheritdoc />
    public async Task SaveAtomicAsync(
        TenantId tenantId,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(entry);
        System.ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowedReaction(
            Harborline.Api.Foundation.IdentityAtlas.Permissions.AuthorizationOperation.Parse(
                Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.LedgerPost),
            tenantId,
            "journal-entry",
            entry.Id.Value);
        if (!entry.TenantId.Equals(tenantId))
        {
            throw new System.ArgumentException(
                $"JournalEntry '{entry.Id.Value}' carries TenantId '{entry.TenantId.Value}' " +
                $"but caller passed tenantId '{tenantId.Value}'.",
                nameof(entry));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Security verdict G-4 (md2 Finding 1) — when an ambient HomeEpochWriteScope is active (a multi-home
        // write asserting the epoch it believes it is home for), the fence read AND the effect commit MUST
        // be one atomic read-through-write so a concurrent promotion cannot commit a higher epoch between
        // them (the TOCTOU). That requires the write lock to be held for the read's duration, which a plain
        // SaveChanges (implicit, write-only transaction) does NOT do. So the fenced path runs the whole
        // unit-of-work inside an explicit BEGIN IMMEDIATE transaction (HomeEpochFenceTransaction): the
        // RESERVED write lock is taken BEFORE the fence read, the read sees the durably-current epoch under
        // that lock, and a stale assertion throws inside the transaction so the JE + audit + idempotency +
        // status rows all roll back. A superseded home commits NOTHING.
        //
        // With no scope the registered fence adapter reports NotApplicable and the write keeps the plain
        // SaveChanges shape, avoiding a write-lock-held transaction it does not need.
        var fenceActive = HomeEpochWriteScope.Current is not null;

        if (fenceActive)
        {
            await HomeEpochFenceTransaction.RunAsync(
                ctx,
                () => StageAndSaveAsync(ctx, entry, decision, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StageAndSaveAsync(ctx, entry, decision, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates and runs the declared adapters, stages the journal entry, and issues the single save.
    /// </summary>
    private async Task StageAndSaveAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken cancellationToken)
    {
        var unitOfWork = new NodeJournalWriteUnitOfWork(ctx, entry, decision);
        await _enlistmentRegistry
            .EnlistAsync(NodeJournalWriteOperation.Post, unitOfWork, cancellationToken)
            .ConfigureAwait(false);
        ctx.Set<JournalEntry>().Add(entry);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalEntry> Snapshot(TenantId tenantId)
    {
        // Synchronous snapshot over a dedicated short-lived context (mirrors the Bridge
        // EfJournalStore — the ambient per-request context may already be disposed at read
        // time). The Lines JSONB column round-trips in the same SELECT, so entries come back
        // fully hydrated and balanced.
        using var ctx = _contextFactory.CreateDbContext();
        return ctx.Set<JournalEntry>()
            .Where(e => e.TenantId == tenantId)
            .AsNoTracking()
            .ToList();
    }

    /// <inheritdoc />
    public async Task<JournalEntry?> FindBySourceReferenceAsync(
        TenantId tenantId,
        string sourceReference,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentException.ThrowIfNullOrEmpty(sourceReference);

        // ADR 0122 §D2 posting idempotency — pre-write lookup over the persisted
        // SourceReference column on the recoverable local-node.db (the dedupe state IS
        // the record, never a seed-keyed KV index). Tenant-scoped WHERE is the node's
        // defence-in-depth boundary (no ambient query filter). The unique index
        // ux_journal_entries_tenant_source_ref makes this a single index seek and is the
        // durable backstop for any race past the JournalPostingService phase-1.5 check.
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ctx.Set<JournalEntry>()
            .Where(e => e.TenantId == tenantId && e.SourceReference == sourceReference)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a persisted entry with <paramref name="updated"/> (matched on
    /// <see cref="JournalEntry.Id"/>). Used by the reverse path to transition the ORIGINAL
    /// entry to <see cref="JournalEntryStatus.Reversed"/> + set its
    /// <see cref="JournalEntry.ReversedBy"/> FK after the reversing entry is saved — the same
    /// state transition <see cref="InMemoryJournalStore.ReplaceEntry"/> performs in-memory and
    /// the Bridge applies for its in-memory store. Without it the Posted-only guard would never
    /// fire on a second reverse, allowing double-counted GL reversals (F-89-A, sec-eng 2026-06-13).
    /// </summary>
    /// <remarks>
    /// No-op (idempotent) when no row matches the id, matching the in-memory contract. The
    /// tenant guard throws <see cref="System.ArgumentException"/> on a mismatched tenant. The
    /// whole replace is one atomic EF transaction.
    /// </remarks>
    public async Task ReplaceEntryAsync(
        TenantId tenantId,
        JournalEntry updated,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(updated);
        if (!updated.TenantId.Equals(tenantId))
        {
            throw new System.ArgumentException(
                $"JournalEntry '{updated.Id.Value}' carries TenantId '{updated.TenantId.Value}' " +
                $"but caller passed tenantId '{tenantId.Value}'.",
                nameof(updated));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Defence-in-depth tenant WHERE clause: never updates a foreign-tenant row even if
        // one somehow shared the id (structurally impossible on a single-device node).
        var exists = await ctx.Set<JournalEntry>()
            .AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && e.Id == updated.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return; // idempotent no-op (matches the in-memory ReplaceEntry contract)
        }

        ctx.Set<JournalEntry>().Update(updated);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
