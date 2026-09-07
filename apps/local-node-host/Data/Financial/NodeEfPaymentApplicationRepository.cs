using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed, <b>read/WRITE</b> <see cref="IPaymentApplicationRepository"/> for the embedded local
/// node (ADR 0122 §D4 T2 — the node payment-WRITE residency; CIC un-deferred 2026-06-16).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="NodeEfPaymentRepository"/>: a read/write EF surface over the recoverable
/// <c>local-node.db</c> for the payment-application rows. <c>DefaultPaymentApplicationService.ApplyAsync</c>
/// calls <see cref="AddAsync"/> here when a node-recorded payment is APPLIED to an invoice/bill — the
/// <see cref="PaymentApplication"/> row is what makes the payment appear (transitively, via the
/// invoice's <c>SubLedgerAccountId</c> FK) in the ADR 0120 lease sub-ledger payment-history. The ADR
/// 0120 <c>SubLedgerReadModel.GetHistoryAsync</c> reads applications from the
/// <see cref="Payment.Applications"/> snapshot collection (hydrated on the <see cref="Payment"/>
/// aggregate read), so this repo backs the by-payment / by-target reads + the apply-time write.
/// </para>
/// <para>
/// <b>WRITE residency (ADR 0122 §D4 T2; CIC un-defer 2026-06-16T1238Z).</b> The mutating members
/// (<see cref="AddAsync"/> / <see cref="ReverseAsync"/>) write ONLY the recoverable <c>local-node.db</c>.
/// </para>
/// <para>
/// <b>SC-4 recoverability.</b> Every write lands ONLY in the recoverable <c>local-node.db</c> — never a
/// seed-keyed / per-team KV log. Touches no kernel CRDT-writer type; cannot enlarge the orphan surface.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> Every read carries an explicit <c>WHERE TenantId = @t</c>.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/>.
/// </para>
/// </remarks>
public sealed class NodeEfPaymentApplicationRepository : IPaymentApplicationRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfPaymentApplicationRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    /// <remarks>
    /// WRITE residency (ADR 0122 §D4 T2). Inserts the application into the recoverable
    /// <c>local-node.db</c>. A tenant mismatch is a caller bug (<see cref="ArgumentException"/>).
    /// </remarks>
    public async Task AddAsync(
        TenantId tenantId, PaymentApplication application, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"PaymentApplication '{application.Id.Value}' carries TenantId '{application.TenantId.Value}' " +
                $"but caller passed tenantId '{tenantId.Value}'.",
                nameof(application));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ctx.Set<PaymentApplication>().Add(application);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// PPI-2 reversal residency. Retains the original application, inserts a linked contra row, and
    /// stamps the original in one database transaction. Returns null on missing, foreign-tenant, or
    /// contra-row input; never erases allocation evidence. Ticket 095: the outcome is discriminated,
    /// so a caller can tell "I recorded this reversal" from "someone else already had".
    /// </remarks>
    public async Task<PaymentApplicationReversalResult> ReverseAsync(
        TenantId tenantId,
        PaymentApplicationId id,
        Instant reversedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ctx.Set<PaymentApplication>()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return PaymentApplicationReversalResult.NotFound;
        }
        if (existing.ReversesApplicationId is not null)
        {
            return PaymentApplicationReversalResult.NotFound;
        }
        if (existing.ReversedByApplicationId is not null)
        {
            return PaymentApplicationReversalResult.AlreadyReversed(existing);
        }

        var reversal = PaymentApplication.CreateReversal(existing, reversedAtUtc);
        var reversed = existing.MarkReversed(reversal.Id, reversedAtUtc);
        ctx.Entry(existing).CurrentValues.SetValues(reversed);
        ctx.Set<PaymentApplication>().Add(reversal);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PaymentApplicationReversalResult.Recorded(reversed);
    }

    /// <inheritdoc />
    public async Task<PaymentApplication?> GetAsync(
        TenantId tenantId, PaymentApplicationId id, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PaymentApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentApplication>> ListByPaymentAsync(TenantId tenantId, PaymentId paymentId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PaymentApplication>()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.PaymentId == paymentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentApplication>> ListByTargetAsync(TenantId tenantId, string targetId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<PaymentApplication>()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.TargetId == targetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
