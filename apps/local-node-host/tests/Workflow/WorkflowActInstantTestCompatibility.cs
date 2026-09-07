using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// Supplies a deterministic instant to pre-ticket unit fixtures. Production surfaces still require the
/// boundary instant, and the kernel-clock integration tests pass their frozen instant explicitly.
/// </summary>
internal static class WorkflowActInstantTestCompatibility
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    internal static Task CreateInstanceAsync(
        this IWorkflowStore store,
        WorkflowInstanceRecord instance,
        CancellationToken ct = default)
        => store.CreateInstanceAsync(instance, At, ct);

    internal static Task AdvanceAsync(
        this IWorkflowStore store,
        WorkflowStepKey key,
        WorkflowEffect? effect,
        string resultJson,
        string eventType,
        string eventDataJson,
        string nextStep,
        WorkflowStatus nextStatus,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
        => store.AdvanceAsync(
            key, effect, resultJson, eventType, eventDataJson, nextStep, nextStatus, at ?? At, ct);

    internal static Task ParkAsync(
        this IWorkflowStore store,
        string instanceId,
        string step,
        string reasonJson,
        int iteration = 0,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
        => store.ParkAsync(instanceId, step, reasonJson, at ?? At, iteration, ct);

    internal static Task<string> StartInvoiceApprovalAsync(
        this NodeWorkflowInstantiationService service,
        TenantId tenantId,
        string invoiceId,
        decimal amount,
        string debitAccount,
        string creditAccount,
        string memo,
        CancellationToken ct = default)
        => service.StartInvoiceApprovalAsync(
            tenantId, invoiceId, amount, debitAccount, creditAccount, memo, At, ct);

    internal static Task<int> SyncRecurringGenerationProcessesAsync(
        this NodeWorkflowInstantiationService service,
        TenantId tenantId,
        CancellationToken ct = default)
        => service.SyncRecurringGenerationProcessesAsync(tenantId, At, ct);
}
