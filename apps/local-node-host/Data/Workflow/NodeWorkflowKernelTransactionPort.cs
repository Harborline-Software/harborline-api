using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Kernel.Core;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// Owns the kernel boundary for one durable workflow advance.  Its held node transaction is also the
/// transaction joined by financial effects, so the boundary has exactly one commit point.
/// </summary>
internal sealed class NodeWorkflowKernelTransactionPort
    : IKernelPreparedTransactionPort<NodeWorkflowAdvance, WorkflowInstanceRecord>
{
    private readonly LocalNodeDbContext _context;

    /// <summary>Constructs the port over the context whose transaction owns the workflow advance.</summary>
    internal NodeWorkflowKernelTransactionPort(LocalNodeDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>The context on which a workflow effect stages its joined journal command.</summary>
    internal LocalNodeDbContext Context => _context;

    /// <inheritdoc />
    public async ValueTask<IKernelPreparedTransaction<NodeWorkflowAdvance, WorkflowInstanceRecord>> BeginAsync(
        CancellationToken cancellationToken = default) =>
        new Transaction(_context, await HomeEpochFenceTransaction.BeginAsync(_context, cancellationToken).ConfigureAwait(false));

    private sealed class Transaction(LocalNodeDbContext context, HomeEpochFenceTransaction.Held fence)
        : IKernelPreparedTransaction<NodeWorkflowAdvance, WorkflowInstanceRecord>
    {
        private KernelOperationIdentity? _operation;
        private NodeWorkflowAdvance? _advance;
        private bool _audited;

        public ValueTask StageOperationAsync(KernelOperationIdentity operation, CancellationToken cancellationToken = default)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            return ValueTask.CompletedTask;
        }

        public async ValueTask StageRecordAsync(NodeWorkflowAdvance advance, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(advance);
            if (_operation?.CommandId != advance.Key.Value)
                throw new InvalidOperationException("The workflow advance does not match its kernel operation identity.");

            var instance = await context.Set<WorkflowInstanceRecord>()
                .FirstOrDefaultAsync(row => row.Id == advance.Key.InstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Workflow instance '{advance.Key.InstanceId}' was not found during advance.");
            var sequence = await NextSequenceAsync(context, advance.Key.InstanceId, cancellationToken).ConfigureAwait(false);
            context.Set<WorkflowEventRecord>().Add(new WorkflowEventRecord
            {
                InstanceId = advance.Key.InstanceId, Seq = sequence, Step = advance.Key.Step,
                EventType = advance.EventType, DataJson = string.IsNullOrEmpty(advance.EventDataJson) ? "{}" : advance.EventDataJson,
                OccurredAt = advance.At,
            });
            context.Set<WorkflowStepIdempotencyRecord>().Add(new WorkflowStepIdempotencyRecord
            {
                Key = advance.Key.Value, InstanceId = advance.Key.InstanceId, Step = advance.Key.Step,
                Iteration = advance.Key.Iteration, ResultJson = string.IsNullOrEmpty(advance.ResultJson) ? "{}" : advance.ResultJson,
                CompletedAt = advance.At,
            });
            instance.CurrentStep = advance.NextStep;
            instance.Status = advance.NextStatus;
            instance.UpdatedAt = advance.At;
            _advance = advance;
        }

        public async ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audit);
            var advance = _advance ?? throw new InvalidOperationException("The workflow advance must be staged before its audit.");
            var payload = JsonSerializer.Serialize(new { event_type = "Workflow.Advanced", step = advance.Key.Step, next_step = advance.NextStep, next_status = advance.NextStatus, result = advance.ResultJson });
            // Flush joined audit rows under the held transaction so the persisted tip includes them.
            // This saves staged writes without committing; a later audit or commit failure rolls them back.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var previous = await context.Set<NodeAuditEventRow>().FromSql($"""
                SELECT * FROM node_audit_events WHERE "TenantId" = {advance.TenantId} ORDER BY rowid DESC LIMIT 1
                """).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            context.Set<NodeAuditEventRow>().Add(new NodeAuditEventRow
            {
                AuditId = audit.AuditId, TenantId = advance.TenantId, EventType = "Workflow.Advanced", OccurredAt = audit.RecordedAt,
                Actor = audit.ActorId, Payload = payload, PrevHash = previous?.Hash,
                Hash = NodeAuditHashChain.ComputeHash(previous?.Hash, audit.AuditId, "Workflow.Advanced", audit.ActorId, advance.TenantId, audit.RecordedAt, payload),
            });
            _audited = true;
        }

        public async ValueTask<WorkflowInstanceRecord> CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_advance is null || !_audited) throw new InvalidOperationException("A workflow advance and its audit are required.");
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await fence.CommitAsync(cancellationToken).ConfigureAwait(false);
            return context.Set<WorkflowInstanceRecord>().Local.Single(row => row.Id == _advance.Key.InstanceId);
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => new(fence.RollbackAsync(cancellationToken));
        public ValueTask DisposeAsync() => fence.DisposeAsync();

        private static async Task<long> NextSequenceAsync(LocalNodeDbContext context, string instanceId, CancellationToken cancellationToken)
        {
            var sequence = await context.Set<WorkflowEventRecord>().Where(row => row.InstanceId == instanceId)
                .Select(row => (long?)row.Seq).MaxAsync(cancellationToken).ConfigureAwait(false);
            return sequence.GetValueOrDefault(-1) + 1;
        }
    }
}

/// <summary>One workflow advance prepared for the kernel transaction boundary.</summary>
internal sealed record NodeWorkflowAdvance(
    WorkflowStepKey Key,
    string TenantId,
    string ResultJson,
    string EventType,
    string EventDataJson,
    string NextStep,
    WorkflowStatus NextStatus,
    DateTimeOffset At)
{
    /// <summary>Builds the deterministic operation identity from the step and observable advance result.</summary>
    internal KernelOperationIdentity Operation() => new(
        Key.Value,
        Key.Value,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { event_type = "Workflow.Advanced", next_step = NextStep, next_status = NextStatus.ToString(), result = ResultJson })))));
}
