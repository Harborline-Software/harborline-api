using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Boot-time <b>home-epoch genesis backstop</b> (ADR 0101 Rev 3.2 precondition 2 / ADR 0168
/// D2-A4(a)): at install / first enrollment — the host's first start with an active team — writes
/// the signed genesis <see cref="HomeEpochRecord"/> (epoch 1, home = THIS installing node) for the
/// active tenant, so a single-device install is a RECORDED fact, never an inferred absence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The origin gate — a POSITIVE claim, not an absence.</b> ADR 0168 D2-A4(a) rejects "no local
/// epoch row" as a write precondition: a JOINER device (wire-enrolled via
/// <see cref="NodeEnrollmentJoinService"/>, its bootstrap adopting the inviter's team) also has an
/// empty local table for a tenant that is ALREADY homed on the inviter — self-writing "home = ME"
/// there would be a forged origin. The gate is the team's own trust chain: the genesis is written
/// ONLY when the active team's roster genesis FOUNDER key (<see cref="NodeTeamRoster"/>.Current —
/// the chain root of <c>MemberRoster.StableGenesis</c>) equals THIS node's signing principal
/// (<see cref="NodePrincipalSigner.NodePublicKey"/>), and the roster's team is the ACTIVE team. On
/// the installing device the two keys are the same root-seed-derived key; on a wire-join adopter
/// the roster's founder is the INVITER's key, the comparison fails, and the service logs a named
/// refusal and skips — never a silent no-op, and never a write.
/// </para>
/// <para>
/// <b>Coverage boundary.</b> This gate closes the WIRE-JOIN vector (an adopted roster's chain root
/// is not this node). A residual remains out of its reach: an operator who pins a foreign
/// <c>LocalNode:TeamId</c> onto a fresh install whose LOCAL roster seeding then self-founds under
/// that team would still pass — that shape is a configuration-trust question, not an enrollment
/// one, and is out of proportion for this boot backstop.
/// </para>
/// <para>
/// <b>Registration order.</b> Registered AFTER <see cref="MultiTeamBootstrapHostedService"/> (hosted
/// services start in registration order), so the active team is set by the time <c>StartAsync</c>
/// runs — the same posture as <see cref="Governance.HostedGovernanceGenesisService"/>. If no team is
/// active yet (a composition that seeds no team), this is a calm no-op and the next start with an
/// active team writes the genesis.
/// </para>
/// <para>
/// <b>Idempotent + restart-safe.</b> The write goes through
/// <see cref="HomeEpochGenesis.EnsureWrittenAsync"/>: an existing epoch (this service on a previous
/// boot, or a later promotion) means no-op — restarting the host or re-running enrollment never
/// mints a second genesis. Scope is the genesis write ONLY: no multi-home promotion is triggered
/// here (MD-3/MD-4 deferred), and the G-4 fence behaviour is unchanged (it reads the tip row; a
/// genesis tip of "this device" fences nothing new on a single-device install).
/// </para>
/// </remarks>
public sealed class HostedHomeEpochGenesisService : IHostedService
{
    private readonly IHomeEpochStore _store;
    private readonly NodePrincipalSigner _signer;
    private readonly NodeTeamRoster _teamRoster;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedHomeEpochGenesisService> _logger;

    /// <summary>Constructs the genesis backstop over the home-epoch store, the node principal
    /// signer (identity + signature), the install-level team roster (the origin gate's founder-key
    /// source), and the active-team accessor.</summary>
    public HostedHomeEpochGenesisService(
        IHomeEpochStore store,
        NodePrincipalSigner signer,
        NodeTeamRoster teamRoster,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedHomeEpochGenesisService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _teamRoster = teamRoster ?? throw new ArgumentNullException(nameof(teamRoster));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            _logger.LogInformation(
                "Home-epoch genesis backstop: no active team yet — deferring; the genesis is " +
                "written on the first start with an active team.");
            return;
        }

        // ── The origin gate (ADR 0168 D2-A4(a)) ──────────────────────────────────────────────────
        // Write only on a POSITIVE origin claim: this node's signing principal must BE the roster
        // genesis founder of the team it would home. A wire-join adopter fails this comparison (the
        // adopted chain root is the INVITER's key) and is refused by name. Cheap defense first: the
        // roster we consult must actually be the ACTIVE team's roster, or the founder comparison
        // would be answering a question about a different team.
        var roster = _teamRoster.Current;
        if (roster.TeamId != active.TeamId.Value)
        {
            _logger.LogWarning(
                "Home-epoch genesis backstop REFUSED: the install roster is for team " +
                "'{RosterTeamId}' but the active team is '{ActiveTeamId}' — refusing to home a " +
                "tenant against a foreign roster.",
                roster.TeamId, active.TeamId.Value);
            return;
        }
        var founderKey = roster.PublicKeyOf(roster.GenesisPartyId);
        var founderKeyB64 = founderKey?.ToBase64Url();
        if (!string.Equals(founderKeyB64, _signer.NodePublicKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Home-epoch genesis backstop REFUSED: this node's principal ({NodeKey}) is not the " +
                "roster genesis founder ({FounderKey}) of team '{TeamId}' — wire-join adopter " +
                "posture; a joiner never self-writes 'home = me'.",
                _signer.NodePublicKey, founderKeyB64 ?? "<unresolved>", active.TeamId);
            return;
        }

        var tenantId = ActiveTeamTenantContext.ProjectTenantId(active.TeamId).Value;
        var at = _timeProvider.GetUtcNow();
        var wrote = await HomeEpochGenesis.EnsureWrittenAsync(
            _store, _signer.Signer, tenantId, _signer.NodePublicKey, at, cancellationToken)
            .ConfigureAwait(false);

        if (wrote)
        {
            _logger.LogInformation(
                "Home-epoch genesis backstop: wrote signed genesis (epoch 1) for tenant " +
                "'{TenantId}' naming this node ({HomeDeviceId}) as the home.",
                tenantId, _signer.NodePublicKey);
        }
        else
        {
            _logger.LogInformation(
                "Home-epoch genesis backstop: tenant '{TenantId}' already has a home epoch — no-op.",
                tenantId);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
