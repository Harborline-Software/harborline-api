using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed, <b>read/WRITE</b> <see cref="IPaymentRepository"/> for the embedded local node
/// (ADR 0122 §D4 T2 — the node payment-WRITE residency; CIC un-deferred payment-write 2026-06-16; the
/// read half landed with P2 → (b); ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns payment READ + WRITE residency.</b> The node carries the <c>payments</c>
/// SCHEMA (contributed by <c>PaymentsEntityModule</c> into <see cref="LocalNodeDbContext"/>,
/// <c>Applications</c> as JSONB) and a payments read-plane (<see cref="Health.PaymentRoutes"/> projects
/// <see cref="LocalNodeDbContext"/>.<c>Set&lt;Payment&gt;()</c> directly). This repository is the
/// <see cref="IPaymentRepository"/>-shaped read/write surface that (a) the ADR 0120 sub-ledger
/// projection (<c>SubLedgerReadModel</c>) needs to resolve a lease's payment-history offline, and (b)
/// the node payment-write routes + <c>DefaultPaymentApplicationService</c> use to RECORD and APPLY a
/// payment fully offline — it mirrors <see cref="NodeEfBillRepository"/> / <see cref="NodeEfInvoiceRepository"/>
/// over the SAME recoverable SQLCipher store (<c>local-node.db</c>, the C1-durable source of truth).
/// </para>
/// <para>
/// <b>WRITE residency (ADR 0122 §D4 T2; CIC un-defer 2026-06-16T1238Z).</b> The mutating members
/// (<see cref="AddAsync"/> / <see cref="UpdateAsync"/>) write ONLY the recoverable <c>local-node.db</c>.
/// <see cref="AddAsync"/> is idempotent on <see cref="Payment.SourceReference"/> (mirrors the P1
/// JournalEntry posting-idempotency): a re-driven record carrying the same source reference resolves to
/// the existing payment rather than minting a duplicate (the durable backstop is the
/// <c>ux_payments_tenant_source_ref</c> unique partial index). Payment authoring is the rent/invoice/bill
/// payment-recording surface; clearing / bouncing (GL-posting payment lifecycle) remains the deferred
/// future feature (the <c>IPaymentPostingService</c> Clear/Bounce path is NOT wired node-side in T2).
/// </para>
/// <para>
/// <b>SC-4 recoverability.</b> Every write lands ONLY in the recoverable, Store-DEK-enveloped
/// <c>local-node.db</c> — never a seed-keyed / per-team KV log. It touches no kernel CRDT-writer type
/// (<c>PostingEngine</c>/<c>ILedgerEventStream</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>) and
/// cannot enlarge the orphan surface (<c>Sc4RecoverabilityGuardTests</c> Layer-1 IL scan + Layer-2
/// DI-graph stay green).
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> <see cref="LocalNodeDbContext"/> applies NO ambient tenant query
/// filter, so every read carries an explicit <c>WHERE TenantId = @t</c> (defence-in-depth per ADR 0092
/// §A3). The callers pass the active-team-derived tenant (<c>NodeTenant.Resolve(activeTeam)</c> /
/// <c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), so the explicit <c>WHERE TenantId</c> is the
/// per-org isolation predicate — switching the active org switches the rows, no fixed <c>"local"</c>.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/> (mirrors <see cref="NodeEfBillRepository"/> /
/// <see cref="NodeEfInvoiceRepository"/> / <see cref="NodeEfJournalStore"/>) — the ambient scoped
/// context may already be disposed at call time, so the repo never holds one.
/// </para>
/// </remarks>
public sealed class NodeEfPaymentRepository : IPaymentRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfPaymentRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    /// <remarks>
    /// WRITE residency (ADR 0122 §D4 T2; CIC un-defer 2026-06-16). Inserts the payment into the
    /// recoverable <c>local-node.db</c>. <b>Idempotent on <see cref="Payment.SourceReference"/></b>: when
    /// a non-null source reference already exists for this tenant (a re-driven record / network retry),
    /// the existing payment is left intact and no duplicate is created — the AddAsync is a no-op
    /// (the route resolves the existing payment via <see cref="FindBySourceReferenceAsync"/> first).
    /// The durable backstop is the <c>ux_payments_tenant_source_ref</c> unique partial index. A tenant
    /// mismatch is a caller bug (<see cref="ArgumentException"/>), mirroring <see cref="NodeEfBillRepository"/>.
    /// </remarks>
    public async Task AddAsync(
        TenantId tenantId, Payment payment, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (!payment.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Payment '{payment.Id.Value}' carries TenantId '{payment.TenantId.Value}' but caller " +
                $"passed tenantId '{tenantId.Value}'.",
                nameof(payment));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Idempotency dedupe (ADR 0122 §D4 T2 — SourceReference ALONE, mirrors the P1 JournalEntry
        // phase-1.5 dedupe). A non-null SourceReference already present for this tenant means a
        // re-driven record — leave the existing payment intact (no duplicate). Null SourceReference
        // never dedupes (manual records carry their own (tenant,chart,number) uniqueness).
        if (!string.IsNullOrEmpty(payment.SourceReference))
        {
            var existing = await ctx.Set<Payment>()
                .AsNoTracking()
                .AnyAsync(
                    p => p.TenantId == tenantId && p.SourceReference == payment.SourceReference,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing)
            {
                return;
            }
        }

        ctx.Set<Payment>().Add(payment);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// WRITE residency (ADR 0122 §D4 T2). Replaces an existing payment in the recoverable
    /// <c>local-node.db</c> (the apply path updates <see cref="Payment.UnappliedAmount"/> + status).
    /// A same-id row under a different tenant is a caller bug (<see cref="ArgumentException"/>); an
    /// unknown id is an <see cref="InvalidOperationException"/> — mirrors <see cref="NodeEfBillRepository"/>.
    /// </remarks>
    public async Task UpdateAsync(
        TenantId tenantId, Payment payment, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (!payment.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Payment '{payment.Id.Value}' carries TenantId '{payment.TenantId.Value}' but caller " +
                $"passed tenantId '{tenantId.Value}'.",
                nameof(payment));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ctx.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == payment.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            throw new InvalidOperationException($"Payment '{payment.Id.Value}' not found; cannot update.");
        }
        if (!existing.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Payment id '{payment.Id.Value}' already exists under a different tenant.",
                nameof(payment));
        }

        ctx.Set<Payment>().Update(payment);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Payment?> GetAsync(
        TenantId tenantId, PaymentId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Uniform-404: missing OR foreign-tenant returns null via the same WHERE.
        return await ctx.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Payment?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalRef))
        {
            return null;
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId
                  && p.ChartId == chartId
                  && p.ExternalRef == externalRef,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Payment>> ListByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ChartId == chartId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // Newest-first by PaymentDate. Sort in memory: SQLite has no native DateOnly ordering parity
        // across providers, and the single-tenant node set is small (mirrors PaymentRoutes).
        return rows.OrderByDescending(p => p.PaymentDate).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Payment>> ListByPartyAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId partyId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ChartId == chartId && p.PartyId == partyId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.OrderByDescending(p => p.PaymentDate).ToList();
    }

    /// <inheritdoc />
    public async Task<Payment?> FindBySourceReferenceAsync(
        TenantId tenantId,
        string sourceReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sourceReference))
        {
            return null;
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Uniform-404: missing OR foreign-tenant returns null via the same WHERE. Backed by the
        // ux_payments_tenant_source_ref unique partial index (at most one match).
        return await ctx.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId && p.SourceReference == sourceReference,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
