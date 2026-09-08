using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Boot-time <b>genesis backstop</b> for the tenant governance state (ADR 0144 AD.1 — "the instance is born
/// in setup phase"). On startup — after the multi-team bootstrap has seeded the active team — this ensures
/// the active tenant has a <see cref="TenantGovernanceState"/>, writing <see cref="SetupPhase.Setup"/> if
/// none exists yet (idempotent, via <see cref="ITenantGovernanceStateStore.EnsureGenesisAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, not lazy.</b> The lifecycle read route also ensures genesis on first read, but this
/// service makes "genesis writes setup" a boot guarantee independent of whether the Harborline App has polled yet
/// — so a headless node is correctly in setup phase from the moment it boots. The first-run wizard (slices
/// A2/A4) also calls <c>EnsureGenesisAsync</c> on genesis completion; because the write is idempotent, all
/// three callers (this service, the route, the wizard) compose without double-writing or re-opening setup.
/// </para>
/// <para>
/// <b>Registration order.</b> Registered AFTER <c>MultiTeamBootstrapHostedService</c> so the active team is
/// set by the time this <c>StartAsync</c> runs. If no team is active yet (a composition that seeds no team),
/// this is a calm no-op — the route backstop still establishes genesis on first read once a tenant exists.
/// </para>
/// </remarks>
public sealed class HostedGovernanceGenesisService : IHostedService
{
    private readonly ITenantGovernanceStateStore _store;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedGovernanceGenesisService> _logger;

    /// <summary>Constructs the genesis backstop over the governance store + the active-team accessor.</summary>
    public HostedGovernanceGenesisService(
        ITenantGovernanceStateStore store,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedGovernanceGenesisService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            _logger.LogInformation(
                "Governance genesis backstop: no active team yet — deferring; the lifecycle route " +
                "establishes genesis (phase=setup) on first read once a tenant exists.");
            return;
        }

        var tenantId = ActiveTeamTenantContext.ProjectTenantId(active.TeamId).Value;
        var state = await _store.EnsureGenesisAsync(tenantId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Governance genesis backstop: tenant '{TenantId}' is in {Phase} phase (born {EnteredSetupAt:O}).",
            tenantId, SetupPhaseWire.ToWire(state.SetupPhase), state.EnteredSetupAt);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
