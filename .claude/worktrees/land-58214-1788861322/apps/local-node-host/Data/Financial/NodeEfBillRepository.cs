using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IBillRepository"/> for the embedded local node
/// (Cohort D Step 2c — the AP node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns AP bill data.</b> Before Cohort D Step 2c the node carried the
/// <c>bills</c> SCHEMA (contributed by <c>ApEntityModule</c> into <see cref="LocalNodeDbContext"/>,
/// Lines as JSONB) but had NO <see cref="IBillRepository"/> registration and no bill writes — so
/// AP was Bridge-dependent. This is the FIRST EF-backed <see cref="IBillRepository"/> in the fleet:
/// the Bridge ran on the in-memory <c>InMemoryBillRepository</c> because AP persistence was
/// D5-fenced (the fence is now LIFTED). It gives the node a write/read surface over the SAME
/// SQLCipher financial store (<c>local-node.db</c>, the C1-durable source of truth) the journal
/// store + payments read-plane already use, so a single-device install no longer needs
/// signal-bridge for AP.
/// </para>
/// <para>
/// <b>Mirrors the <see cref="InMemoryBillRepository"/> contract exactly</b>, with the backing store
/// being <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher) instead of a concurrent dictionary:
/// uniform-404 on cross-tenant / tombstoned / missing reads, <see cref="ArgumentException"/> on a
/// tenant-mismatched write, idempotent soft-delete, and a Version bump on tombstone. Audit emission
/// is the durable-layer concern (ADR 0104 §7 X-AUDIT) and is deliberately deferred — matching the
/// node JE store + the audit-free <c>InMemoryBillRepository()</c> ctor + the financial-cluster
/// durable-layer pattern (the row's presence in the keyed SQLCipher store is the audit record).
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> <see cref="LocalNodeDbContext"/> applies NO ambient tenant
/// query filter, so every read here carries an explicit <c>WHERE TenantId = @t</c> (defence-in-depth
/// per ADR 0092 §A3) and every write asserts <c>bill.TenantId == tenantId</c>. The routes pass the
/// active-team-derived tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>;
/// ADR 0032 identity layer), so the explicit <c>WHERE TenantId</c> is the per-org isolation predicate —
/// switching the active org switches which org's bills are visible, no fixed <c>"local"</c> sentinel.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/> (mirrors <see cref="NodeEfJournalStore"/> /
/// <see cref="NodeEfAccountResolver"/>) — the ambient scoped context may already be disposed at
/// call time, so the repo never holds one.
/// </para>
/// </remarks>
public sealed class NodeEfBillRepository : IBillRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfBillRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(
        TenantId tenantId, Bill bill, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (!bill.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Bill '{bill.Id.Value}' carries TenantId '{bill.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(bill));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Existing-row checks mirror InMemoryBillRepository: a tombstoned target rejects further
        // mutation; a same-id row under a different tenant is a caller bug. The defence-in-depth
        // WHERE TenantId narrows the existence probe to this tenant; a row that exists ONLY under a
        // foreign tenant therefore reads as "no existing row" here AND we re-check the global id
        // below to honour the cross-tenant-id guard.
        var existing = await ctx.Set<Bill>()
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bill.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.DeletedAtUtc is not null)
            {
                throw new InvalidOperationException(
                    $"Bill '{bill.Id.Value}' is tombstoned; further mutations are not permitted.");
            }
            if (!existing.TenantId.Equals(tenantId))
            {
                throw new ArgumentException(
                    $"Bill id '{bill.Id.Value}' already exists under a different tenant.",
                    nameof(bill));
            }

            // Update in place (EF tracks by key on a fresh context).
            ctx.Set<Bill>().Update(bill);
        }
        else
        {
            ctx.Set<Bill>().Add(bill);
        }

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Bill?> GetAsync(
        TenantId tenantId, BillId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Uniform-404: missing, tombstoned, OR foreign-tenant all return null via the same WHERE.
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.Id == id && b.TenantId == tenantId && b.DeletedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Bill?> GetByVendorBillNumberAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId vendorId,
        string billNumber,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.DeletedAtUtc == null
                  && b.TenantId == tenantId
                  && b.ChartId == chartId
                  && b.VendorId == vendorId
                  && b.BillNumber == billNumber,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Bill?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.DeletedAtUtc == null
                  && b.TenantId == tenantId
                  && b.ChartId == chartId
                  && b.ExternalRef == externalRef,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bill>> ListByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .Where(b => b.DeletedAtUtc == null && b.TenantId == tenantId && b.ChartId == chartId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bill>> ListByVendorAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId vendorId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .Where(b => b.DeletedAtUtc == null
                     && b.TenantId == tenantId
                     && b.ChartId == chartId
                     && b.VendorId == vendorId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bill>> ListBySubLedgerAccountAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Defence-in-depth: filter on BOTH SubLedgerAccountId AND TenantId (ADR 0092 / 0120 PR-C).
        return await ctx.Set<Bill>()
            .AsNoTracking()
            .Where(b => b.DeletedAtUtc == null
                     && b.TenantId == tenantId
                     && b.SubLedgerAccountId == subLedgerAccountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bill>> QueryOpenAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId? vendorId = null,
        string? propertyId = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // BillStatus.IsOpen() is an extension method and cannot translate to SQL; express the open
        // set inline so the predicate runs server-side over the value-converted Status column.
        var query = ctx.Set<Bill>()
            .AsNoTracking()
            .Where(b => b.DeletedAtUtc == null
                     && b.TenantId == tenantId
                     && b.ChartId == chartId
                     && (b.Status == BillStatus.Received
                         || b.Status == BillStatus.Approved
                         || b.Status == BillStatus.PartiallyPaid));

        if (vendorId is not null)
        {
            var vid = vendorId.Value;
            query = query.Where(b => b.VendorId == vid);
        }
        if (propertyId is not null)
        {
            query = query.Where(b => b.PropertyId == propertyId);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        TenantId tenantId,
        BillId id,
        PartyId actor,
        string? reason,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Tenant-scoped lookup: an unknown id OR a foreign-tenant row both return false (uniform-404).
        var bill = await ctx.Set<Bill>()
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (bill is null)
        {
            return false;
        }
        if (bill.DeletedAtUtc is not null)
        {
            return true; // idempotent: already tombstoned
        }

        var now = new Instant(admittedAt);
        var tombstoned = bill with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = bill.Version + 1,
        };
        ctx.Set<Bill>().Update(tombstoned);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
