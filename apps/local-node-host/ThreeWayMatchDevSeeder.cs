using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// DEV-ONLY three-way-match workflow seeder (ADR 0135 A1 / W-8). On a development node it author→admit→persist→
/// publishes the <see cref="NodeThreeWayMatchWorkflowSeed"/> definition, then instantiates + drives a couple of
/// runs so the Harborline App "Effect Confirmations" surface renders REAL interpreter-parked CP confirmations (and the
/// executed-run report renders a REAL completed run) instead of a mock — the first production driver of the
/// otherwise-built-but-untriggered declarative interpreter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The demo realization of the ADR 0135 A1 interpreter — no new ADR.</b> The interpreter + the broker-PEP +
/// the ledger.post-journal-entry effect factory + the server-side confirmation context are already built +
/// DI-registered (<see cref="NodeWorkflowComposition.AddNodeDeclarativeWorkflowExecution"/>). This seeder is the
/// first driver: it PARKS one three-way-match run on the human approval (for the confirmations surface) and
/// drives one run to completion via a real <c>approve</c> (for the executed-run report + a real posted JE).
/// </para>
/// <para>
/// <b>The dev gate is AIRTIGHT — fail-safe OFF.</b> The seed runs ONLY when <c>IsDevelopment()</c> is true.
/// A Production host never drives these runs, so no real tenant ledger is polluted.
/// </para>
/// <para>
/// <b>Resilient — never crashes the node.</b> Every step is best-effort under try/catch: a seed failure logs a
/// warning and returns (a dev demo must never take down the host). Idempotent — <c>EnsurePublishedAsync</c> +
/// the derived instance ids are no-ops on a re-run; a run already past its park is left as-is.
/// </para>
/// </remarks>
public sealed class ThreeWayMatchDevSeeder : IHostedService
{
    private readonly AuthorizedWorkflowDefinitionLifecycle _definitionStore;
    private readonly IWorkflowStore _workflowStore;
    private readonly IWorkflowTriggerDispatcher _dispatcher;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ThreeWayMatchDevSeeder> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly AuthorizationGate _authorizationGate;

    /// <summary>Constructs the dev seeder over the workflow definition + instance stores + the trigger dispatcher.</summary>
    public ThreeWayMatchDevSeeder(
        AuthorizedWorkflowDefinitionLifecycle definitionStore,
        IWorkflowStore workflowStore,
        IWorkflowTriggerDispatcher dispatcher,
        IActiveTeamAccessor activeTeam,
        IHostEnvironment environment,
        ILogger<ThreeWayMatchDevSeeder> logger,
        AuthorizationGate authorizationGate,
        TimeProvider? timeProvider = null)
    {
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _authorizationGate = authorizationGate ?? throw new ArgumentNullException(nameof(authorizationGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return;
        }

        TenantId tenant;
        try
        {
            tenant = NodeTenant.Resolve(_activeTeam);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThreeWayMatchDevSeeder: could not resolve the active-team tenant — skipping the demo seed.");
            return;
        }

        try
        {
            var authority = new AuthorizationWriteContext(
                new ActorId(AccessGrantAuthorizationSeed.DevWorkflowSeederPrincipal),
                tenant,
                _timeProvider.GetUtcNow());
            var decision = await _definitionStore.DecideAsync(
                NodeThreeWayMatchWorkflowSeed.DefinitionKey, authority, cancellationToken).ConfigureAwait(false);
            await NodeThreeWayMatchWorkflowSeed.EnsurePublishedAsync(
                    _definitionStore, tenant.Value, decision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThreeWayMatchDevSeeder: could not publish the three-way-match definition — skipping the demo seed.");
            return;
        }

        // Run A — PARK a confirmation for the "Effect Confirmations" surface (matched → review, no JE).
        await SeedParkedAsync(tenant, billId: "bill-demo-001", amount: 4200m,
            memo: "Vendor bill — office equipment (3-way matched)", cancellationToken).ConfigureAwait(false);

        // Run B — drive a run to completion via a real approve (→ posted JE) for the executed-run report.
        var completedInstance = await SeedParkedAsync(tenant, billId: "bill-demo-002", amount: 1875m,
            memo: "Vendor bill — maintenance parts (3-way matched)", cancellationToken).ConfigureAwait(false);
        if (completedInstance is not null)
        {
            try
            {
                var instance = await _workflowStore.LoadAsync(completedInstance, cancellationToken).ConfigureAwait(false);
                if (instance is null)
                    return;
                var authority = DevAuthority(tenant);
                var workflowDecision = await DecideAsync(
                    authority, TeamRolePermissions.RecordsWrite, "record", instance.Id, cancellationToken)
                    .ConfigureAwait(false);
                var ledgerDecision = await DecideAsync(
                    authority, TeamRolePermissions.LedgerPost, "journal-entry",
                    NodeLedgerPostingEffect.JournalEntryIdFor(instance).Value, cancellationToken)
                    .ConfigureAwait(false);
                await _dispatcher.DispatchAsync(
                    WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, completedInstance,
                        NodeThreeWayMatchWorkflowSeed.ReviewState, authority.At, "{\"decision\":\"approve\"}"),
                    new WorkflowDispatchAuthority(workflowDecision, ledgerDecision),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ThreeWayMatchDevSeeder: could not approve the demo run {InstanceId} — leaving it parked.", completedInstance);
            }
        }

        _logger.LogInformation(
            "ThreeWayMatchDevSeeder: seeded the three-way-match demo for tenant {Tenant} — one parked CP " +
            "confirmation + one completed run (executed-run report).", tenant.Value);
    }

    /// <summary>
    /// Instantiates a three-way-match run and drives its <c>matched</c> event so it parks on the human approval.
    /// Returns the instance id on success (parked), or <see langword="null"/> on any failure (logged). Idempotent
    /// on the derived instance id.
    /// </summary>
    private async Task<string?> SeedParkedAsync(TenantId tenant, string billId, decimal amount, string memo, CancellationToken ct)
    {
        try
        {
            var authority = DevAuthority(tenant);
            var instanceId = await NodeThreeWayMatchWorkflowSeed.InstantiateAsync(
                _workflowStore, tenant, billId: billId, amount: amount,
                expenseAccount: "6000", payableAccount: "2000", memo: memo, at: authority.At, ct).ConfigureAwait(false);

            var decision = await DecideAsync(
                authority, TeamRolePermissions.RecordsWrite, "record", instanceId, ct).ConfigureAwait(false);
            await _dispatcher.DispatchAsync(
                WorkflowTrigger.For(
                    WorkflowTriggerKind.Event,
                    instanceId,
                    NodeThreeWayMatchWorkflowSeed.MatchingState,
                    authority.At),
                decision,
                ct).ConfigureAwait(false);

            return instanceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThreeWayMatchDevSeeder: could not seed the demo run for bill {BillId}.", billId);
            return null;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private AuthorizationWriteContext DevAuthority(TenantId tenant) =>
        new(new ActorId(AccessGrantAuthorizationSeed.DevWorkflowSeederPrincipal), tenant, _timeProvider.GetUtcNow());

    private async ValueTask<AuthorizationDecision> DecideAsync(
        AuthorizationWriteContext authority,
        string operation,
        string kind,
        string id,
        CancellationToken ct)
    {
        var decision = await _authorizationGate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(operation), kind, id), ct).ConfigureAwait(false);
        decision.RequireAllowed();
        return decision;
    }
}
