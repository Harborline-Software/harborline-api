using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// The SINGLE source of truth for the install's <b>genesis team id</b> — the deterministic team identifier
/// that the seeded roster genesis, the <c>/admission/invites</c> trust anchor, AND the daemon's active team
/// must ALL agree on. Resolving the genesis team id through this one helper is what keeps
/// <c>daemon-active-team == roster-genesis-team == invite-advertised-team</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this closes.</b> The roster genesis (Program.cs) + the invite anchor
/// (<c>TeamTrustAnchor.FromRoster</c>) derive the team id deterministically from the install's root seed —
/// <see cref="Derive"/> — when <c>LocalNode:TeamId</c> is unset. But the legacy single-team bootstrap
/// (<see cref="MultiTeamBootstrapHostedService"/>) used to synthesize a FRESH <c>Guid.NewGuid()</c> for the
/// active team on an unset <c>LocalNode:TeamId</c>. The gossip daemon then bound to that RANDOM team while the
/// invite advertised the SEED-DERIVED genesis team — so a remote joiner joined a team the daemon was not
/// gossiping on and cross-machine deltas never met. The Harborline App's <c>node_supervisor.rs</c> does not pin
/// <c>LocalNode__TeamId</c> (it injects only the root seed + <c>MultiTeam__Enabled=false</c>), so the real
/// GUI path hit this; the hardware verify only worked because its harness pinned <c>LocalNode__TeamId</c> to
/// the genesis team. Routing BOTH the genesis seeding and the bootstrap active-team default through
/// <see cref="Resolve"/> collapses the three to one id by construction.
/// </para>
/// <para>
/// <b>That claim was only half true until earlier repository ticket #3450.</b> The paragraph above described the LEGACY
/// SINGLE-TEAM branch, which is the one that consumes <see cref="GenesisTeamIdProvider"/>. The MULTI-TEAM
/// branch activates each configured <c>TeamBootstraps[].TeamId</c> directly and never called
/// <see cref="Resolve"/> at all, so on a multi-team host with <c>LocalNode:TeamId</c> unset the three ids
/// did NOT collapse — the node served one team while the signed roster genesis and the invite anchor sat
/// under another, and invitations could never resolve. The committed dogfood config takes that branch, so
/// customer-zero was in exactly this state. <see cref="Resolve"/> now consults the multi-team section too.
/// Recorded here because a doc that promises an invariant the code does not deliver is what the next author
/// reasons from.
/// </para>
/// <para>
/// <b>The multi-team branch is fail-closed.</b> The bootstrap materializes every configured team and selects
/// <c>TeamBootstraps[0]</c> as active, while this resolver owns genesis selection. The bootstrap compares its
/// first team with the injected resolved value before materializing anything and refuses startup on drift. The
/// coupling test also compares the two paths, so a future sort, dedupe, or skipped entry cannot silently move the
/// daemon away from its trust anchor. This is why
/// <c>GenesisTeamBootstrapTests.Bootstrap_active_team_agrees_with_the_resolved_genesis_on_the_multi_team_branch</c>
/// asserts the two against each other rather than either against a literal.
/// </para>
/// <para>
/// <b>Derivation.</b> The first 16 bytes of the install's 32-byte root seed, folded verbatim into a
/// <see cref="System.Guid"/> — the same derivation Program.cs has always used to seed the roster genesis. The
/// root seed is itself the stable per-install identity (keystore-backed on a direct install, injected by a
/// Bridge supervisor for a spawned tenant), so the team id is stable across restarts and unique per install
/// without persisting a separate value. An explicit <c>LocalNode:TeamId</c> override always wins (a node
/// enrolled into a provisioned team pins it); the override is idempotent — both the genesis and the bootstrap
/// resolve to the SAME configured value through this helper.
/// </para>
/// </remarks>
public static class GenesisTeamId
{
    /// <summary>
    /// Derive the deterministic seed-derived genesis team id from the install's root seed: the first 16 bytes
    /// of the seed folded into a <see cref="System.Guid"/>. This is the fallback used when
    /// <c>LocalNode:TeamId</c> is not configured.
    /// </summary>
    /// <param name="rootSeed">The resolved 32-byte install root seed (the node's stable identity material).</param>
    /// <exception cref="System.ArgumentException"><paramref name="rootSeed"/> is shorter than 16 bytes.</exception>
    public static TeamId Derive(System.ReadOnlySpan<byte> rootSeed)
    {
        if (rootSeed.Length < 16)
        {
            throw new System.ArgumentException(
                $"Root seed must be at least 16 bytes to derive the genesis team id; got {rootSeed.Length}.",
                nameof(rootSeed));
        }

        return new TeamId(new System.Guid(rootSeed[..16].ToArray()));
    }

    /// <summary>
    /// Resolve the install's genesis team id, in precedence order: the configured
    /// <paramref name="configuredTeamId"/> (<c>LocalNode:TeamId</c>) when present and parseable; else the
    /// FIRST configured multi-team bootstrap when multi-team is enabled; else the seed-derived value from
    /// <see cref="Derive"/>. This is the canonical resolution the roster genesis seeding and the bootstrap
    /// active-team default both call so the two never diverge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the multi-team rung exists (earlier repository ticket #3450).</b> The invariant this type documents —
    /// <c>daemon-active-team == roster-genesis-team == invite-anchor-team</c> — was enforced only on the
    /// LEGACY SINGLE-TEAM branch, which consumes <see cref="GenesisTeamIdProvider"/>. The multi-team branch
    /// activates each configured <c>TeamBootstraps[].TeamId</c> directly and never consulted this helper, so
    /// with multi-team on and <c>LocalNode:TeamId</c> unset the node served one team while the signed roster
    /// genesis and the invite anchor were seeded under a different, seed-derived one. On such a host
    /// <c>AccountSetupInvitationIssuer</c> and <c>WebRosterGranterAuthorityProvider</c> cannot resolve the
    /// acting party in the roster and <c>VerifiedTenantRosterReader</c> refuses <c>WrongTenant</c> —
    /// <b>invitations could never work</b>. That was found on the deployed dogfood host, whose committed
    /// config takes exactly this branch. Delivering the promise by construction beats documenting the
    /// footgun.
    /// </para>
    /// <para>
    /// <b>Blast radius.</b> A configured <c>LocalNode:TeamId</c> still wins, so any node pinned to a
    /// provisioned team is untouched; and the new rung is gated on multi-team being ENABLED, so every Harborline App
    /// install is untouched too (<c>node_supervisor.rs</c> pins <c>LocalNode__MultiTeam__Enabled=false</c> and
    /// sets <c>LocalNode__TeamId</c> nowhere). The only shape this changes is multi-team-on-with-TeamId-unset.
    /// An operator who deliberately accepted the divergence pins <c>LocalNode:TeamId</c> to the seed-derived
    /// value before upgrading, which makes this a no-op for them — but that is an opt-OUT, not a safe harbour.
    /// On a multi-team host that pin KEEPS genesis != active-team, which is precisely the state this doc calls
    /// "invitations could never work", and it trips <c>FounderTenantMembershipAttachStatus.TenantDiverged</c>.
    /// It buys an unchanged upgrade, not a working one.
    /// </para>
    /// <para>
    /// <b>Configured pin consistency.</b> Rung 1 wins for a configured pin. The multi-team bootstrap compares its
    /// element-0 active-team selection with this resolved value before it materializes any team and refuses startup
    /// if they differ, naming both ids. A host therefore cannot boot with genesis on the pin and its daemon on a
    /// different configured bootstrap.
    /// </para>
    /// <para>
    /// <b>It is NOT free, and the earlier claim that it was is retracted.</b> This once read "why this needs no
    /// data migration", reasoning that the only affected shape is already broken so re-homing it costs nothing.
    /// Flipping the genesis team DOES have a cost, and it was paid on the one host this affects. Because
    /// <c>MemberRoster.StableGenesis</c> is deterministic in (team, party, founder key), a new team id mints a
    /// SECOND genesis record rather than replacing the first — and <c>RosterCrdtProjection</c> rejects any
    /// rebuild that sees two, so the roster CRDT stops converging and the node falls back to its locally
    /// seeded roster. That is exactly what happened on the deployed dogfood host when an operator pinned
    /// <c>LocalNode__TeamId</c> by hand: the duplicate-genesis warning appears in its log two and a half
    /// minutes after the service environment was rewritten, and every earlier boot back to 2026-07-06 is
    /// clean. The founder DM key and team-scoped transport key also move with the team id
    /// (<c>HKDF(root, genesisTeamId)</c>), though on that host nothing had been sealed under the old ones.
    /// </para>
    /// <para>
    /// So the honest statement is: this change re-homes nothing by itself on a host that is already pinned,
    /// and it makes the pin unnecessary and redeploy-durable. On an UNPINNED divergent host it performs the
    /// same flip an operator would, with the same stale-genesis consequence — which must be cleaned up
    /// separately, because purging a signed roster record is a trust-anchor change and does not belong in a
    /// resolution fix.
    /// </para>
    /// </remarks>
    /// <param name="rootSeed">The resolved 32-byte install root seed.</param>
    /// <param name="configuredTeamId">The optional <c>LocalNode:TeamId</c> configuration value.</param>
    /// <param name="multiTeam">
    /// The bound <c>LocalNode:MultiTeam</c> section, when the host has one. Consulted ONLY when it is enabled
    /// and carries at least one bootstrap; a null or disabled section leaves the seed-derived fallback in
    /// place, which is what keeps single-team installs on their existing genesis.
    /// <para>
    /// <b>Deliberately NOT defaulted.</b> A <c>= null</c> here would let a future call site silently get
    /// pre-earlier repository ticket #3450 behaviour by simply not knowing about the parameter — which is exactly how this bug
    /// existed in the first place (the multi-team branch never consulted this helper at all). On a resolver
    /// that decides the roster trust anchor, an explicit <c>null</c> at each call site is worth the keystrokes.
    /// </para>
    /// </param>
    public static TeamId Resolve(
        System.ReadOnlySpan<byte> rootSeed,
        string? configuredTeamId,
        MultiTeamOptions? multiTeam)
    {
        if (!string.IsNullOrWhiteSpace(configuredTeamId) &&
            System.Guid.TryParse(configuredTeamId, out var parsed))
        {
            return new TeamId(parsed);
        }

        if (multiTeam is { Enabled: true, TeamBootstraps.Count: > 0 })
        {
            return new TeamId(multiTeam.TeamBootstraps[0].TeamId);
        }

        return Derive(rootSeed);
    }
}

/// <summary>
/// DI context holder for the resolved <see cref="Harborline.Api.LocalNodeHost.GenesisTeamId"/>. Registered as a singleton
/// in the composition root (Program.cs) after the genesis team id is resolved from the root seed +
/// <c>LocalNode:TeamId</c>, so the legacy single-team <see cref="MultiTeamBootstrapHostedService"/> can seed
/// the SAME id as its active team instead of minting a random one — closing the daemon-vs-invite team-id
/// divergence. Carries only the public team id (no seed material).
/// </summary>
/// <param name="TeamId">The resolved genesis team id — identical to the seeded roster genesis + the invite anchor.</param>
public sealed record GenesisTeamIdProvider(TeamId TeamId);
