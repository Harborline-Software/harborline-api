using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ExecutionRuntime;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ExecutionRuntime;

/// <summary>Adapts the platform receipt/trace port to the encrypted node database.</summary>
/// <remarks>
/// SQLite's non-deferred write transaction takes the writer reservation before reading state.
/// Competing hosts therefore decide against the committed predecessor, including on first insert;
/// an exact replay never saves. The transaction covers validation, save and commit, so a refusal
/// cannot leave a partial write. Reads deliberately retain the pinned reference's empty kindless result.
/// </remarks>
public sealed class NodeExecutionReceiptStore(IDbContextFactory<LocalNodeDbContext> contextFactory) : IExecutionReceiptStore
{
    /// <inheritdoc />
    public async ValueTask PutReceiptAsync(ExecutionReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await Receipts(db, receipt.TenantId, receipt.RunId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            if (!string.Equals(stored.AccessDecisionId, receipt.AccessDecisionId, StringComparison.Ordinal))
            {
                throw new ExecutionRuntimeRefusedException(ExecutionRuntimeRefusals.AccessDecisionChanged,
                    $"Run '{receipt.RunId}' already links another Access decision.");
            }
            var previous = ReadStatus(stored.Status);
            if (previous == receipt.Status)
            {
                return;
            }
            RunStatusTransitions.Require(previous, receipt.Status);
            stored.Status = RunStatusTransitions.WireName(receipt.Status);
        }
        else
        {
            db.Set<ExecutionReceiptRow>().Add(new()
            {
                TenantId = receipt.TenantId.Value, RunKind = receipt.RunId.Kind.Value, RunValue = receipt.RunId.Value,
                Status = RunStatusTransitions.WireName(receipt.Status), AccessDecisionId = receipt.AccessDecisionId,
            });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionReceipt?> GetReceiptAsync(TenantId tenantId, RunId runId, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await Receipts(db, tenantId, runId).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return stored is null ? null : new ExecutionReceipt(tenantId, runId, ReadStatus(stored.Status), stored.AccessDecisionId);
    }

    /// <inheritdoc />
    public async ValueTask AppendTraceStepAsync(TenantId tenantId, RunId runId, ExecutionTraceStep traceStep, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (runId.Kind.Value is null)
        {
            throw new ExecutionRuntimeRefusedException(ExecutionRuntimeRefusals.TraceStepInvalid, "A trace requires a run identity.");
        }
        ArgumentNullException.ThrowIfNull(traceStep);
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var steps = Traces(db, tenantId, runId);
        var existing = await steps.SingleOrDefaultAsync(row => row.Ordinal == traceStep.Ordinal, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Phase == traceStep.Phase && existing.Status == traceStep.Status)
        {
            return;
        }
        var last = await steps.MaxAsync(row => (int?)row.Ordinal, cancellationToken).ConfigureAwait(false) ?? 0;
        if (traceStep.Ordinal != last + 1)
        {
            throw new ExecutionRuntimeRefusedException(ExecutionRuntimeRefusals.TraceStepOutOfOrder,
                $"Run '{runId}' expects trace ordinal {last + 1}, not {traceStep.Ordinal}.");
        }
        db.Set<ExecutionTraceStepRow>().Add(new()
        {
            TenantId = tenantId.Value, RunKind = runId.Kind.Value, RunValue = runId.Value,
            Ordinal = traceStep.Ordinal, Phase = traceStep.Phase, Status = traceStep.Status,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExecutionTraceStep>> GetTraceAsync(TenantId tenantId, RunId runId, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Traces(db, tenantId, runId).AsNoTracking().OrderBy(row => row.Ordinal)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new ExecutionTraceStep(row.Ordinal, row.Phase, row.Status)).ToArray();
    }

    private static IQueryable<ExecutionReceiptRow> Receipts(LocalNodeDbContext db, TenantId tenant, RunId run) =>
        db.Set<ExecutionReceiptRow>().Where(row => row.TenantId == tenant.Value && row.RunKind == run.Kind.Value && row.RunValue == run.Value);

    private static IQueryable<ExecutionTraceStepRow> Traces(LocalNodeDbContext db, TenantId tenant, RunId run) =>
        db.Set<ExecutionTraceStepRow>().Where(row => row.TenantId == tenant.Value && row.RunKind == run.Kind.Value && row.RunValue == run.Value);

    private static RunStatus ReadStatus(string wireName) =>
        Enum.GetValues<RunStatus>().Single(status => RunStatusTransitions.WireName(status) == wireName);

    private static void RequireTenant(TenantId tenant)
    {
        if (tenant.IsSystemSentinel)
        {
            throw new ArgumentException("An execution receipt or trace requires a tenant.", nameof(tenant));
        }
    }
}
