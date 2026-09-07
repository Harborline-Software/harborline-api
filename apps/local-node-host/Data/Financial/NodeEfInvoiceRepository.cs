using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IInvoiceRepository"/> for the embedded local node
/// (Cohort D Step 2b — the AR node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns AR invoice data.</b> Before Cohort D Step 2b the node carried the
/// <c>invoices</c> SCHEMA (contributed by <c>ArEntityModule</c> into <see cref="LocalNodeDbContext"/>,
/// Lines as JSONB) but had NO <see cref="IInvoiceRepository"/> registration and no invoice writes —
/// so AR was Bridge-dependent. This is the FIRST EF-backed <see cref="IInvoiceRepository"/> in the
/// fleet (the Bridge ran the <c>EfInvoiceRepository</c> over Npgsql; the node runs over SQLite/
/// SQLCipher). It gives the node a write/read surface over the SAME SQLCipher financial store
/// (<c>local-node.db</c>, the C1-durable source of truth) the journal store + bills + payments
/// read-plane already use, so a single-device install no longer needs signal-bridge for AR.
/// </para>
/// <para>
/// <b>Mirrors the <see cref="InMemoryInvoiceRepository"/> contract exactly</b>, with the backing
/// store being <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher) instead of a concurrent
/// dictionary: uniform-404 on cross-tenant / tombstoned / missing reads, <see cref="ArgumentException"/>
/// on a tenant-mismatched write, idempotent soft-delete, a Version bump on tombstone, and the
/// TOCTOU-guarded <see cref="SoftDeleteIfStatusAsync"/> (compare-and-act on the status before
/// tombstoning a Draft, so a concurrent Issue can't strand a GL-posted invoice). Audit emission is
/// the durable-layer concern (ADR 0104 §7 X-AUDIT) and is deliberately deferred — matching the node
/// JE store + bill repo + the audit-free <c>InMemoryInvoiceRepository()</c> ctor + the
/// financial-cluster durable-layer pattern (the row's presence in the keyed SQLCipher store is the
/// audit record).
/// </para>
/// <para>
/// <b>Canonical InvoiceNumber format guard (ADR 0092-adjacent invariant).</b> Like
/// <see cref="InMemoryInvoiceRepository"/>, a non-Draft invoice upserted here MUST carry a
/// well-formed <c>INV-YYYY-MM-DD-{Replica}-{NNNN}</c> number (<see cref="InvoiceNumberFormat"/>);
/// a malformed number on an Issued+ invoice throws <see cref="InvalidOperationException"/>. Drafts
/// may carry any number. (The node create path mints the canonical number AT CREATE time via
/// <see cref="NodeEfInvoiceNumberingService"/> — mirroring the Bridge create flow — so even the
/// Draft already carries a real number and the EF unique index
/// <c>ux_invoices_tenant_chart_number</c> is never asked to store two empty-string drafts.)
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> <see cref="LocalNodeDbContext"/> applies NO ambient tenant
/// query filter, so every read here carries an explicit <c>WHERE TenantId = @t</c> (defence-in-depth
/// per ADR 0092 §A3) and every write asserts <c>invoice.TenantId == tenantId</c>. The routes pass the
/// active-team-derived tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>;
/// ADR 0032 identity layer), so the explicit <c>WHERE TenantId</c> is the per-org isolation predicate —
/// switching the active org switches which org's invoices are visible, no fixed <c>"local"</c> sentinel.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/> (mirrors <see cref="NodeEfJournalStore"/> /
/// <see cref="NodeEfBillRepository"/> / <see cref="NodeEfAccountResolver"/>) — the ambient scoped
/// context may already be disposed at call time, so the repo never holds one.
/// </para>
/// </remarks>
public sealed class NodeEfInvoiceRepository : IInvoiceRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfInvoiceRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(
        TenantId tenantId, Invoice invoice, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (!invoice.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Invoice '{invoice.Id.Value}' carries TenantId '{invoice.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(invoice));
        }

        // Non-Draft invoices MUST match the canonical numbering format (mirrors
        // InMemoryInvoiceRepository — a malformed number on an Issued+ invoice signals a bad
        // importer payload or a hand-rolled Invoice.Create). Drafts may carry any number.
        if (invoice.Status != InvoiceStatus.Draft
            && !InvoiceNumberFormat.IsWellFormed(invoice.InvoiceNumber))
        {
            throw new InvalidOperationException(
                $"Invoice '{invoice.Id.Value}' is in status '{invoice.Status}' but its InvoiceNumber " +
                $"'{invoice.InvoiceNumber}' does not match the canonical format " +
                "'INV-YYYY-MM-DD-{Replica}-{NNNN}'.");
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Existing-row checks mirror InMemoryInvoiceRepository: a tombstoned target rejects further
        // mutation; a same-id row under a different tenant is a caller bug. The probe is global on id
        // (not tenant-narrowed) so the cross-tenant-id guard below can fire.
        var existing = await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoice.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.DeletedAtUtc is not null)
            {
                throw new InvalidOperationException(
                    $"Invoice '{invoice.Id.Value}' is tombstoned; further mutations are not permitted.");
            }
            if (!existing.TenantId.Equals(tenantId))
            {
                throw new ArgumentException(
                    $"Invoice id '{invoice.Id.Value}' already exists under a different tenant.",
                    nameof(invoice));
            }

            // Update in place (EF tracks by key on a fresh context).
            ctx.Set<Invoice>().Update(invoice);
        }
        else
        {
            ctx.Set<Invoice>().Add(invoice);
        }

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetAsync(
        TenantId tenantId, InvoiceId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Uniform-404: missing, tombstoned, OR foreign-tenant all return null via the same WHERE.
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.Id == id && i.TenantId == tenantId && i.DeletedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetByNumberAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.DeletedAtUtc == null
                  && i.TenantId == tenantId
                  && i.ChartId == chartId
                  && i.InvoiceNumber == invoiceNumber,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetByExternalRefAsync(
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
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.DeletedAtUtc == null
                  && i.TenantId == tenantId
                  && i.ChartId == chartId
                  && i.ExternalRef == externalRef,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Invoice>> ListByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .Where(i => i.DeletedAtUtc == null && i.TenantId == tenantId && i.ChartId == chartId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Invoice>> ListByCustomerAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId customerId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .Where(i => i.DeletedAtUtc == null
                     && i.TenantId == tenantId
                     && i.ChartId == chartId
                     && i.CustomerId == customerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Invoice>> ListBySubLedgerAccountAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Defence-in-depth: filter on BOTH SubLedgerAccountId AND TenantId (ADR 0092 / 0120 PR-C).
        return await ctx.Set<Invoice>()
            .AsNoTracking()
            .Where(i => i.DeletedAtUtc == null
                     && i.TenantId == tenantId
                     && i.SubLedgerAccountId == subLedgerAccountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        TenantId tenantId,
        InvoiceId id,
        PartyId actor,
        string? reason,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Tenant-scoped lookup: an unknown id OR a foreign-tenant row both return false (uniform-404).
        var invoice = await ctx.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (invoice is null)
        {
            return false;
        }
        if (invoice.DeletedAtUtc is not null)
        {
            return true; // idempotent: already tombstoned
        }

        var now = new Instant(admittedAt);
        var tombstoned = invoice with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = invoice.Version + 1,
        };
        ctx.Set<Invoice>().Update(tombstoned);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool?> SoftDeleteIfStatusAsync(
        TenantId tenantId,
        InvoiceId id,
        PartyId actor,
        string? reason,
        InvoiceStatus requiredStatus,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var invoice = await ctx.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (invoice is null)
        {
            return false; // unknown OR foreign-tenant (uniform-404)
        }
        if (invoice.DeletedAtUtc is not null)
        {
            return true; // already tombstoned — idempotent
        }

        // TOCTOU guard (mirrors InMemoryInvoiceRepository / sec-eng F-2 2026-06-12): re-check status
        // at delete time. A concurrent Issue between the handler's status check and this call can flip
        // Draft → Issued; tombstoning a GL-posted invoice would strand its journal entry. Return null
        // so the handler maps to 409 Conflict.
        if (invoice.Status != requiredStatus)
        {
            return null;
        }

        var now = new Instant(admittedAt);
        var tombstoned = invoice with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = invoice.Version + 1,
        };
        ctx.Set<Invoice>().Update(tombstoned);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Hard-delete a <b>Draft</b> invoice (true row removal from <c>local-node.db</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Doctrine: Invoice-Draft is the ONE entity in the fleet that gets a true hard-DELETE. A Draft
    /// has no GL footprint (no journal entry has been posted), so removing the row is safe. Issued,
    /// Voided, and WrittenOff invoices have a GL footprint and MUST be closed via void / write-off
    /// instead — callers that attempt to delete a non-Draft receive <c>null</c> and should return 422.
    /// </para>
    /// <para>
    /// Returns:
    /// <list type="bullet">
    ///   <item><c>true</c> — row removed (or already absent — idempotent).</item>
    ///   <item><c>false</c> — id not found OR foreign-tenant (uniform-404 invariant; no diagnostic
    ///         leak).</item>
    ///   <item><c>null</c> — invoice exists + belongs to the tenant but is NOT
    ///         <see cref="InvoiceStatus.Draft"/> at delete time (TOCTOU guard: a concurrent Issue can
    ///         flip Draft → Issued between the handler's status check and this call; caller maps to
    ///         422 Unprocessable).</item>
    /// </list>
    /// </para>
    /// <para>
    /// SC4-T9(b): only touches <c>local-node.db</c> — the recoverable SQLCipher store. No kernel CRDT
    /// write, no cross-cluster event, no journal-entry side-effect (a Draft carries none).
    /// </para>
    /// </remarks>
    public async Task<bool?> HardDeleteIfDraftAsync(
        TenantId tenantId,
        InvoiceId id,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Load with tracking so EF can issue a DELETE (no AsNoTracking here).
        var invoice = await ctx.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (invoice is null)
        {
            return false; // unknown OR foreign-tenant (uniform-404 invariant)
        }

        // TOCTOU guard: re-check status inside the EF transaction context. A concurrent Issue between
        // the handler's status-check read and this call can flip Draft → Issued, at which point the
        // invoice has a GL footprint and must NOT be hard-deleted.
        if (invoice.Status != InvoiceStatus.Draft)
        {
            return null; // caller maps to 422 Unprocessable
        }

        // Tombstoned Drafts: a soft-deleted Draft row IS still a Draft at the status level (no GL
        // footprint was ever posted), so a hard-delete of a tombstoned Draft is allowed — it just
        // finishes the removal. The tombstone only reflects logical deletion; the row is still
        // physically present until we remove it here.
        ctx.Set<Invoice>().Remove(invoice);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
