using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The <c>schedule</c>-trigger daemon (ADR 0135 D1) — a <see cref="BackgroundService"/> that periodically
/// asks the <see cref="IWorkflowScheduleSource"/> for due occurrences and dispatches each through the
/// <see cref="IWorkflowTriggerDispatcher"/>. The single home-resident scheduler that drives recurring /
/// timer-parked processes forward without a human or an inbound event.
/// </summary>
/// <remarks>
/// <para>
/// <b>At-least-once is safe.</b> A schedule tick may dispatch a trigger that already advanced (e.g. the
/// previous tick advanced it but the daemon restarted before recording the tick) — the dispatcher's
/// idempotency guard turns that into a no-op (<see cref="WorkflowDispatchResult.ReplayedNoOp"/>), so the
/// daemon never double-effects.
/// </para>
/// <para>
/// <b>Single-owner (ADR 0135 D3).</b> The daemon runs on the tenant home — the only node that advances
/// invariant-bearing instances. v1 is single-device, so there is exactly one daemon; multi-device handoff
/// is home failover (deferred to the residency deliverable), not a competing scheduler.
/// </para>
/// <para>
/// <b>Tick exceptions are logged, not fatal.</b> A failure dispatching one tick is caught + logged so the
/// daemon keeps ticking — a durable engine must not die because one occurrence threw. The instance's own
/// state is unchanged by a failed advance (the atomic transaction rolled back), so the next tick retries.
/// </para>
/// </remarks>
public sealed class WorkflowScheduleDaemon : BackgroundService
{
    private readonly IWorkflowScheduleSource _source;
    private readonly IWorkflowTriggerDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkflowScheduleDaemon> _logger;
    private readonly TimeSpan _interval;
    private readonly AuthorizationGate? _authorizationGate;
    private readonly IWorkflowStore? _store;
    private readonly IWorkflowDefinitionExecutionStore? _definitions;

    /// <summary>The narrow principal used only for independently initiated workflow schedule acts.</summary>
    public const string SchedulerPrincipal = Harborline.Api.Blocks.AccessGrant.AccessGrantAuthorizationSeed.SchedulerPrincipal;

    /// <summary>The default poll interval when none is supplied.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>Constructs the daemon over the schedule source, the dispatcher, the node clock, and a logger.</summary>
    /// <param name="interval">Poll interval; defaults to <see cref="DefaultInterval"/> when null.</param>
    public WorkflowScheduleDaemon(
        IWorkflowScheduleSource source,
        IWorkflowTriggerDispatcher dispatcher,
        TimeProvider time,
        ILogger<WorkflowScheduleDaemon> logger,
        TimeSpan? interval = null,
        AuthorizationGate? authorizationGate = null,
        IWorkflowStore? store = null,
        IWorkflowDefinitionExecutionStore? definitions = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval ?? DefaultInterval;
        _authorizationGate = authorizationGate;
        _store = store;
        _definitions = definitions;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown
            }
            catch (Exception ex)
            {
                // Never let one tick kill the daemon — log and keep ticking.
                _logger.LogError(ex, "Workflow schedule daemon tick failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One tick: fetch the due triggers as of now and dispatch each. Public so a test can drive a single
    /// tick deterministically without waiting on the timer.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var at = _time.GetUtcNow();
        var due = await _source.GetDueTriggersAsync(at, ct).ConfigureAwait(false);
        foreach (var trigger in due)
        {
            ct.ThrowIfCancellationRequested();
            if (_authorizationGate is null || _store is null)
            {
                // Direct-construction typed-handler tests may omit authorization dependencies. Declarative
                // production composition supplies both; its dispatcher refuses this legacy overload.
                await _dispatcher.DispatchAsync(trigger, ct).ConfigureAwait(false);
                continue;
            }

            var instance = await _store.LoadAsync(trigger.InstanceId, ct).ConfigureAwait(false);
            if (instance is null)
            {
                await _dispatcher.DispatchAsync(trigger, ct).ConfigureAwait(false);
                continue;
            }

            // The tick's one server clock sample drives due evaluation, decisions and downstream stamps.
            var authority = new AuthorizationWriteContext(
                new ActorId(SchedulerPrincipal),
                new TenantId(instance.TenantId),
                at);
            var workflowDecision = await _authorizationGate.DecideAsync(
                authority.Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
                    "record",
                    instance.Id),
                ct).ConfigureAwait(false);
            workflowDecision.RequireAllowed();

            AuthorizationDecision? ledgerDecision = null;
            if (await DeclaresLedgerEffectAsync(instance, ct).ConfigureAwait(false))
            {
                var journalId = NodeLedgerPostingEffect.JournalEntryIdFor(instance).Value;
                ledgerDecision = await _authorizationGate.DecideAsync(
                    authority.Request(
                        AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
                        "journal-entry",
                        journalId),
                    ct).ConfigureAwait(false);
                ledgerDecision.RequireAllowed();
            }

            await _dispatcher.DispatchAsync(
                trigger,
                new WorkflowDispatchAuthority(workflowDecision, ledgerDecision),
                ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> DeclaresLedgerEffectAsync(
        WorkflowInstanceRecord instance,
        CancellationToken ct)
    {
        if (_definitions is null)
            return false;
        var record = await _definitions.GetAdmittedAsync(
            new DefinitionCoordinates(
                new TenantId(instance.TenantId),
                instance.DefinitionKey,
                instance.DefinitionVersion),
            ct).ConfigureAwait(false);
        var definition = WorkflowDefinitionWireMapper.ToModel(
            record.Authored,
            record.Tenant,
            record.Key,
            record.Version);
        return definition.Actions.Any(action =>
            string.Equals(action.CapabilityRef, NodeLedgerPostingEffect.CapabilityRef, StringComparison.Ordinal));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
