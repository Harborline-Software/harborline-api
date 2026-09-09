using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Wave 6.3.E.2 startup hosted service that materializes the install's teams
/// via <see cref="ITeamContextFactory"/>, opens each team's encrypted store
/// via <see cref="ITeamStoreActivator"/>, and seeds the initial active team
/// on <see cref="IActiveTeamAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Runs BEFORE <see cref="LocalNodeWorker"/> in <c>IHostedService</c>
/// registration order so the worker's <see cref="IActiveTeamAccessor.Active"/>
/// lookup sees a materialized team when it resolves the per-team
/// <c>IGossipDaemon</c>.
/// </para>
/// <para>
/// Two modes per <see cref="MultiTeamOptions.Enabled"/>:
/// <list type="bullet">
///   <item><description><c>Enabled == true</c>: iterate
///     <see cref="MultiTeamOptions.TeamBootstraps"/>; for each, call
///     <c>ITeamContextFactory.GetOrCreateAsync</c> then
///     <c>ITeamStoreActivator.ActivateAsync</c>. The first listed team becomes
///     the initial active team.</description></item>
///   <item><description><c>Enabled == false</c> (legacy single-team): parse
///     <see cref="LocalNodeOptions.TeamId"/> as a Guid, materialize + activate
///     that one team, set it active.</description></item>
/// </list>
/// When <see cref="LocalNodeOptions.TeamId"/> is null/empty under legacy mode,
/// the bootstrap activates the install's SEED-DERIVED GENESIS team id (from the
/// injected <see cref="GenesisTeamIdProvider"/>) — the SAME id the roster genesis
/// + the <c>/admission/invites</c> trust anchor advertise — so the gossip daemon
/// binds to the team a remote joiner is actually invited into. (It used to mint a
/// fresh <c>Guid.NewGuid()</c>, which diverged from the invite-advertised genesis
/// and broke cross-machine convergence: the joiner joined a team the daemon was
/// not gossiping on.) The explicit <c>LocalNode:TeamId</c> override still wins, and
/// is resolved through the same <see cref="GenesisTeamId"/> helper on both the
/// genesis-seeding path and here, so the two stay in lockstep.
/// </para>
/// </remarks>
public sealed class MultiTeamBootstrapHostedService : IHostedService
{
    private readonly ITeamContextFactory _factory;
    private readonly ITeamStoreActivator _activator;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IMutableTeamRegistry _memberships;
    private readonly IOptions<LocalNodeOptions> _options;
    private readonly GenesisTeamIdProvider? _genesisTeam;
    private readonly NodeAdministratorAuthority? _administrators;
    private readonly NodeTeamRoster? _roster;
    private readonly IOperationSigner? _nodeSigner;
    private readonly ILogger<MultiTeamBootstrapHostedService> _logger;

    public MultiTeamBootstrapHostedService(
        ITeamContextFactory factory,
        ITeamStoreActivator activator,
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships,
        IOptions<LocalNodeOptions> options,
        ILogger<MultiTeamBootstrapHostedService> logger,
        GenesisTeamIdProvider? genesisTeam = null,
        NodeAdministratorAuthority? administrators = null,
        NodeTeamRoster? roster = null,
        IOperationSigner? nodeSigner = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _activator = activator;
        _activeTeam = activeTeam;
        _memberships = memberships;
        _options = options;
        _genesisTeam = genesisTeam;
        _administrators = administrators;
        _roster = roster;
        _nodeSigner = nodeSigner;
        _logger = logger;
    }

    /// <summary>
    /// The OS-user actor of this single-office node. Mirrors <see cref="ActiveTeamAuthorizationContext.LocalUserId"/>
    /// (the single-operator user id) so the membership edge, the financial user context, and the contact
    /// write-actor all agree. It is NEVER granted directly: <see cref="ResolveActor"/> returns it only for
    /// the one party this actor IS, so an establishment belonging to some other party is not projected here.
    /// </summary>
    private static readonly ActorId NodeOperator = new(ActiveTeamAuthorizationContext.LocalUserId);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var multi = options.MultiTeam ?? new MultiTeamOptions();

        TeamId? firstTeamId = null;

        if (multi.Enabled && multi.TeamBootstraps.Count > 0)
        {
            var configuredFirstTeam = new TeamId(multi.TeamBootstraps[0].TeamId);
            if (_genesisTeam is { } resolvedGenesis && resolvedGenesis.TeamId != configuredFirstTeam)
            {
                var message =
                    $"Resolved genesis team '{resolvedGenesis.TeamId.Value:D}' differs from the first multi-team "
                    + $"bootstrap '{configuredFirstTeam.Value:D}'. LocalNode:TeamId and "
                    + "LocalNode:MultiTeam:TeamBootstraps[0]:TeamId must identify the same trust anchor.";
                _logger.LogError("{Message}", message);
                throw new InvalidOperationException(message);
            }

            _logger.LogInformation(
                "MultiTeam bootstrap: materializing {Count} team(s)",
                multi.TeamBootstraps.Count);

            foreach (var bootstrap in multi.TeamBootstraps)
            {
                var teamId = new TeamId(bootstrap.TeamId);
                var displayName = !string.IsNullOrWhiteSpace(bootstrap.DisplayName)
                    ? bootstrap.DisplayName!
                    : $"Team {bootstrap.TeamId:D}";

                await _factory.GetOrCreateAsync(teamId, displayName, cancellationToken)
                    .ConfigureAwait(false);
                await _activator.ActivateAsync(teamId, cancellationToken)
                    .ConfigureAwait(false);
                await EstablishOrProjectAdministratorAsync(teamId, displayName, cancellationToken)
                    .ConfigureAwait(false);

                firstTeamId ??= teamId;
            }
        }
        else
        {
            // Legacy single-team mode. The active team MUST be the install's GENESIS team — the SAME id the
            // roster genesis + the /admission/invites trust anchor advertise — or the gossip daemon binds to a
            // team a remote joiner is never invited into and cross-machine deltas never meet (the Harborline App GUI
            // path: node_supervisor.rs injects the root seed + MultiTeam:Enabled=false but does NOT pin
            // LocalNode:TeamId). The composition root resolves the genesis team via GenesisTeamId.Resolve and
            // injects it as GenesisTeamIdProvider, so this path activates the IDENTICAL id (no Guid.NewGuid()).
            //
            // Resolution precedence, both honoring the SAME GenesisTeamId.Resolve contract Program.cs uses:
            //   1. The injected GenesisTeamIdProvider (the production path — already configured-override-aware
            //      AND seed-derived-aware), so daemon-active-team == roster-genesis-team == invite-team.
            //   2. Fallback when the provider is absent (tests that wire the bootstrap in isolation): the
            //      configured LocalNode:TeamId if parseable, else a fresh Guid. A production host ALWAYS
            //      registers the provider, so the fresh-Guid branch is unreachable on the real boot path.
            TeamId teamId;
            if (_genesisTeam is { } provider)
            {
                teamId = provider.TeamId;
                _logger.LogInformation(
                    "Single-team legacy bootstrap: active team {TeamId} (seed-derived genesis — matches the "
                    + "roster genesis + the /admission/invites trust anchor).",
                    teamId);
            }
            else if (!string.IsNullOrWhiteSpace(options.TeamId) &&
                Guid.TryParse(options.TeamId, out var parsed))
            {
                teamId = new TeamId(parsed);
            }
            else
            {
                teamId = TeamId.New();
                _logger.LogWarning(
                    "LocalNode:TeamId not configured and no GenesisTeamIdProvider registered; synthesized "
                    + "{TeamId} for single-team legacy bootstrap. A production host registers the provider so "
                    + "the daemon binds the seed-derived genesis team — this branch is test-only.",
                    teamId);
            }

            var displayName = $"Team {teamId.Value:D}";

            await _factory.GetOrCreateAsync(teamId, displayName, cancellationToken)
                .ConfigureAwait(false);
            await _activator.ActivateAsync(teamId, cancellationToken)
                .ConfigureAwait(false);
            await EstablishOrProjectAdministratorAsync(teamId, displayName, cancellationToken)
                .ConfigureAwait(false);

            firstTeamId = teamId;
        }

        if (firstTeamId is { } seedId)
        {
            await _activeTeam.SetActiveAsync(seedId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Active team set to {TeamId}", seedId);
        }
    }

    /// <summary>
    /// THE INSTALLER PRINCIPAL, and nothing else (ADR 0066 clauses 4, 5, 6 and 9). Establishes the first
    /// administrator ONCE when the installation has none, and on every later boot only PROJECTS the
    /// administrator that already exists into the process-local membership registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this replaces.</b> This method used to re-upsert the OS user as full <c>Admin</c> on EVERY
    /// boot, supplying neither <c>MemberPublicKey</c> nor <c>AdmissionSignature</c> and reading no prior
    /// authority state at all. That was a standing, unaudited, unrevocable re-grant of full administrative
    /// authority — ADR 0066 names it the actual master key in this system, and clause 9 removes it. Because it
    /// re-ran every boot, revoking an administrator did not stick: the next restart minted them back.
    /// </para>
    /// <para>
    /// <b>Establish versus project.</b> Only the ESTABLISHING write is one-shot. Projecting a durable
    /// administrator into the in-memory <see cref="IMutableTeamRegistry"/> happens on every boot and must —
    /// the registry is process-local, so the node would otherwise start with no resolvable authority at all.
    /// The distinction is the whole design: projection reads state, it never creates it.
    /// </para>
    /// <para>
    /// <b>Fail closed when the authority is absent.</b> A host that has not registered the durable authority
    /// (a minimal DI test) enrolls NOBODY. The removed behaviour must not survive as a fallback: "mint an
    /// unsigned Admin when we cannot check" is the defect, restated.
    /// </para>
    /// <para>
    /// <b>The record names the grantee.</b> Both the establishment and the projection are scoped to (team,
    /// party): the team decides which authority applies, and the party decides who receives it. Selecting on
    /// the team alone and granting to a constant actor is the same defect wearing a durable log — a revoked
    /// administrator's authority would reappear on the next boot as somebody else's record projected onto
    /// the same local principal.
    /// </para>
    /// </remarks>
    private async Task EstablishOrProjectAdministratorAsync(
        TeamId teamId,
        string displayName,
        CancellationToken ct)
    {
        if (_administrators is null)
        {
            _logger.LogWarning(
                "administrator.authority_unavailable: no durable administrator authority is registered, so " +
                "team {TeamId} was materialized with no administrator membership. This host cannot establish " +
                "or project administrative authority; use the offline recovery command on a real node.",
                teamId);
            return;
        }

        // Granting authority off a log whose chain does not verify is worse than starting with none: a row
        // inserted by direct SQL would otherwise fold in as a usable administrator and be projected.
        if (!await _administrators.ChainVerifiesAsync(ct).ConfigureAwait(false))
        {
            _logger.LogError(
                "administrator.authority_chain_broken: the administrator-authority log's hash chain does " +
                "not verify, so NOTHING was projected for tenant {TenantId}. The node runs with no " +
                "administrative authority until the offline recovery command establishes one.",
                teamId);
            return;
        }

        var team = teamId.Value.ToString("D");
        var inTeam = (await _administrators.UsableAsync(ct).ConfigureAwait(false))
            .Where(administrator => string.Equals(administrator.TeamId, team, StringComparison.Ordinal))
            .ToArray();

        // The record decides who is granted. Selecting by team alone and then granting to a compile-time
        // actor constant was the ORIGINAL DEFECT, restated: revoke P1, and the next boot would project
        // whatever OTHER party still held authority in that team onto the same local actor with full Admin —
        // so the durable log said "revoked" while the authority the node enforced on was unchanged.
        var existing = inTeam.FirstOrDefault(administrator => ResolveActor(administrator.PartyId) is not null);

        // Establish only when the TEAM has none. A global count made a second team unestablishable.
        if (existing is null && inTeam.Length == 0)
        {
            // Start the installer window's clock on the FIRST boot that sees this state, not on the boot
            // that finally manages to establish — otherwise a node that cannot yet establish gets a fresh
            // window every time, which is a window that never closes.
            await _administrators.ObserveEmptyLogAsync(ct).ConfigureAwait(false);
            existing = await TryEstablishAsync(_administrators, teamId, ct).ConfigureAwait(false);
        }

        if (existing is null || ResolveActor(existing.PartyId) is not { } actor)
        {
            _logger.LogWarning(
                "administrator.not_projected: tenant {TenantId} has {Count} usable administrator(s), none " +
                "of which is a party this node's operator IS, and the installer did not establish one. NO " +
                "membership was granted — the node runs with no administrative authority for this tenant " +
                "until the offline recovery command establishes one.",
                teamId, inTeam.Length);
            return;
        }

        await ProjectAsync(actor, teamId, displayName, existing, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The LIVE roster member this node IS — the member whose bound principal key is this node's own signing
    /// key — or null when the roster carries no such live member.
    /// </summary>
    /// <remarks>
    /// Membership is read from LIVE roster state, so a revoked member is not one: <c>Revoke</c> drops the party
    /// from live state while keeping its admission in the immutable chain, and the genesis party id it does NOT
    /// change. Keying on that id was the defect — the founder stayed "the genesis party" forever, so the boot
    /// re-armed a revoked administrator on the next restart. There is no genesis special case here: a genesis
    /// admission is just the first admission, and what makes a party THIS node is its key, not its ordinal.
    /// </remarks>
    private RosterMember? LocalLiveMember() =>
        _roster is { } roster && _nodeSigner is { } signer
            ? roster.Current.Members.FirstOrDefault(member => member.PublicKey.Equals(signer.IssuerId))
            : null;

    /// <summary>
    /// Resolve the LOCAL ACTOR an establishment grants authority to, from the establishment's OWN party id —
    /// or null when this node is not that party, or the roster no longer carries it live, in which case
    /// nothing is granted.
    /// </summary>
    /// <remarks>
    /// Without a roster (or without the node's signer) there is no mapping at all, and refusing is the only safe
    /// answer — "grant to the constant when we cannot tell" is the defect ADR 0066 clause 9 removes.
    /// </remarks>
    private ActorId? ResolveActor(string partyId) =>
        !string.IsNullOrWhiteSpace(partyId) &&
        LocalLiveMember() is { } local &&
        string.Equals(partyId, local.PartyId, StringComparison.Ordinal)
            ? NodeOperator
            : (ActorId?)null;

    /// <summary>
    /// The installer principal's one permitted act. The gate is re-evaluated inside the writing transaction
    /// by <see cref="NodeAdministratorAuthority"/>; everything decided here is a precondition, never the
    /// decision itself.
    /// </summary>
    private async Task<UsableAdministrator?> TryEstablishAsync(
        NodeAdministratorAuthority administrators,
        TeamId teamId,
        CancellationToken ct)
    {
        // The candidate comes off the SIGNED, genesis-anchored roster admission — never synthesized here.
        // Without a roster there is no admission signature and no member public key, and an establishment
        // carrying neither is exactly the write this migration removes.
        if (_roster is null)
        {
            _logger.LogWarning(
                "administrator.no_signed_admission: no trust roster is registered, so there is no signed " +
                "admission to establish an administrator from. Nothing was written.");
            return null;
        }

        // The party this node IS, read off LIVE membership — not the genesis party id. Establishing the
        // genesis party unconditionally would establish somebody ELSE's party on a node that joined an
        // existing tenant, and would re-establish a founder the roster has already revoked.
        var member = LocalLiveMember();
        if (member is null)
        {
            _logger.LogWarning(
                "administrator.no_signed_admission: the trust roster carries no LIVE member bound to this " +
                "node's signing key. Nothing was written.");
            return null;
        }

        var localParty = member.PartyId;
        var candidate = new AdministratorCandidate(
            TeamId: teamId.Value.ToString("D"),
            PartyId: localParty,
            MemberPublicKey: member.PublicKey.ToBase64Url(),
            AdmissionSignature: member.Admission.Signature,
            AdmittedByPublicKey: member.Admission.AdmittedByPublicKey,
            AdmittedByPartyId: member.Admission.AdmittedByPartyId,
            IsGenesisAdmission: member.Admission.IsGenesis);

        var result = await administrators
            .EstablishByInstallerAsync(candidate, _options.Value.InstallerWindow, ct)
            .ConfigureAwait(false);

        if (result.Applied)
        {
            _logger.LogWarning(
                "administrator.established_by_installer: the installer principal established '{Party}' as " +
                "the first administrator of team {TeamId}, provenance=bootstrap, sequence={Sequence}. This " +
                "happens exactly once per installation and is permanently audited.",
                localParty, teamId, result.Sequence);
        }
        else
        {
            // Every non-applied outcome is a refusal to mint, and all of them are normal on a node that has
            // already been through its bootstrap. AlreadyEstablished in particular is the concurrent-start
            // loser: the winner's row is already committed, so re-reading finds it.
            _logger.LogInformation(
                "administrator.installer_declined: {Code} for team {TeamId}; nothing was written.",
                result.Code, teamId);
        }

        var usable = await administrators.UsableAsync(ct).ConfigureAwait(false);
        return usable.FirstOrDefault(administrator =>
            string.Equals(administrator.TeamId, teamId.Value.ToString("D"), StringComparison.Ordinal) &&
            string.Equals(administrator.PartyId, localParty, StringComparison.Ordinal));
    }

    /// <summary>
    /// Project a durable administrator into the process-local membership registry, carrying the
    /// <c>MemberPublicKey</c> and <c>AdmissionSignature</c> the establishment recorded (ADR 0066 clause 3 —
    /// admission evidence, which is worth keeping precisely because it is not itself authority).
    /// </summary>
    private async Task ProjectAsync(
        ActorId actor,
        TeamId teamId,
        string displayName,
        UsableAdministrator administrator,
        CancellationToken ct)
    {
        var fingerprint = KeyFingerprint.FromPublicKey(teamId.Value.ToByteArray());
        // Seed-grows-into-tree (ADR 0144 D3): the genesis founder holds the full informal-default role graph.
        // The workshop:unlock mode-entry holding (ADR 0144 AD.1) is NOT in this set: the unlock decision reads
        // the access-grant closure (ticket 205 slice 2), so the founder's holding is seeded as a grant by
        // AccessGrantAuthorizationSeed (NodeOperatorRole for NodeOperatorPrincipal) — where the gate reads it.
        // Composing it here as well would be a second, inert copy of the same authority.
        var founderPermissions = PermissionCompositions.ForRole(TeamRole.Admin);
        var membership = new TeamMembership(
            TeamId: teamId.Value,
            DisplayName: displayName,
            RoleDisplayName: TeamRolePermissions.DisplayName(TeamRole.Admin),
            SubkeyFingerprint: fingerprint,
            Role: TeamRole.Admin,
            Permissions: founderPermissions,
            // The two fields the always-on mint never supplied. They come off the durable establishment,
            // which copied them from the signed genesis admission.
            MemberPublicKey: administrator.MemberPublicKey,
            AdmissionSignature: new AdmissionSignature(
                AdmittedByPublicKey: administrator.AdmittedByPublicKey,
                AdmittedByPartyId: administrator.AdmittedByPartyId,
                IssuedAt: administrator.EstablishedAtUtc,
                Nonce: Guid.Empty,
                Signature: administrator.AdmissionSignature,
                IsGenesis: administrator.IsGenesisAdmission));

        await _memberships.AddMembershipAsync(actor, membership, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "administrator.projected: actor '{Actor}' — the local principal that IS party '{Party}' — " +
            "projected as {Role} of tenant {TenantId} from the durable administrator established at " +
            "{Established} with provenance {Provenance}. No authority was created by this boot.",
            actor.Value, administrator.PartyId, TeamRole.Admin, teamId,
            administrator.EstablishedAtUtc, administrator.Provenance);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No-op — TeamContextFactory owns per-team disposal.
        return Task.CompletedTask;
    }
}
