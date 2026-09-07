using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests;

/// <summary>
/// The 3-WAY TEAM-ID MATCH proof: a node with NO <c>LocalNode:TeamId</c> set must boot with its
/// daemon-active team == its seed-derived roster-genesis team == the team its <c>/admission/invites</c>
/// trust anchor advertises. The bug (caught by the cross-machine HW verify) was that
/// <see cref="MultiTeamBootstrapHostedService"/> minted a fresh <c>Guid.NewGuid()</c> for the active team
/// when <c>LocalNode:TeamId</c> was unset — so the gossip daemon bound to a RANDOM team while the invite
/// advertised the SEED-DERIVED genesis team, and a remote joiner joined a team the daemon never gossiped on
/// (cross-machine deltas never met). The Harborline App's <c>node_supervisor.rs</c> does NOT pin
/// <c>LocalNode__TeamId</c> (it injects only the root seed + <c>MultiTeam:Enabled=false</c>), so the real
/// GUI path hit this; the HW verify only worked because its harness pinned <c>LocalNode__TeamId</c>.
/// </summary>
/// <remarks>
/// These tests exercise the EXACT production derivations: the roster genesis via
/// <see cref="GenesisTeamId.Resolve"/> → <see cref="MemberRoster.StableGenesis"/> (the Program.cs path), the
/// invite anchor via <see cref="TeamTrustAnchor.FromRoster"/> (the AdmissionRoutes generate-invite path), and
/// the bootstrap active team via the injected <see cref="GenesisTeamIdProvider"/> (the
/// MultiTeamBootstrapHostedService path). Closing the divergence means all three resolve through the single
/// <see cref="GenesisTeamId"/> helper.
/// </remarks>
public sealed class GenesisTeamBootstrapTests
{
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private static byte[] NewSeed()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    /// <summary>
    /// Build the install's genesis roster the way Program.cs does: resolve the genesis team id off the seed +
    /// the (optional) configured team id, derive the per-node-distinct founder party id off the node key, then
    /// StableGenesis. Returns the roster so a test can read its TeamId AND publish its invite anchor.
    /// </summary>
    private static MemberRoster BuildProductionGenesisRoster(
        byte[] rootSeed,
        string? configuredTeamId,
        MultiTeamOptions? multiTeam = null)
    {
        using var genesisSigner = new NodePrincipalSigner(rootSeed);
        var genesisTeamId = GenesisTeamId.Resolve(rootSeed, configuredTeamId, multiTeam).Value;
        var nodeKeyHex8 = Convert.ToHexString(genesisSigner.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        var partyId = $"os:test#{nodeKeyHex8}";
        return MemberRoster.StableGenesis(
            teamId: genesisTeamId,
            founderPartyId: partyId,
            founderSigner: genesisSigner.Signer,
            verifier: Verifier);
    }

    [Fact]
    public void Seed_derived_genesis_team_matches_roster_genesis_and_invite_anchor()
    {
        var rootSeed = NewSeed();

        // (1) The DAEMON/BOOTSTRAP active team — what MultiTeamBootstrapHostedService now activates via the
        //     GenesisTeamIdProvider (no LocalNode:TeamId set).
        var bootstrapActiveTeam = GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam: null);

        // (2) The ROSTER GENESIS team — what Program.cs seeds the install-level NodeTeamRoster with.
        var genesisRoster = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null);
        var rosterGenesisTeam = new TeamId(genesisRoster.TeamId);

        // (3) The INVITE-ADVERTISED team — what GET/POST /admission/invites publishes via TeamTrustAnchor.
        var anchor = TeamTrustAnchor.FromRoster(genesisRoster);
        var inviteAdvertisedTeam = TeamId.Parse(anchor.TeamId);

        // The whole point of the fix: all three are the SAME id.
        Assert.Equal(rosterGenesisTeam, bootstrapActiveTeam);
        Assert.Equal(inviteAdvertisedTeam, bootstrapActiveTeam);
        Assert.Equal(inviteAdvertisedTeam, rosterGenesisTeam);

        // And it is genuinely the SEED-derived value (the first 16 bytes of the seed → Guid), not random.
        Assert.Equal(new Guid(rootSeed.AsSpan(0, 16).ToArray()), bootstrapActiveTeam.Value);
    }

    [Fact]
    public void Pre_fix_random_active_team_would_diverge_from_invite_team()
    {
        // Demonstrates the BUG the fix removes: the OLD bootstrap minted Guid.NewGuid() for the active team,
        // which (with overwhelming probability) does NOT equal the seed-derived genesis the invite advertises.
        var rootSeed = NewSeed();
        var genesisRoster = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null);
        var inviteAdvertisedTeam = TeamId.Parse(TeamTrustAnchor.FromRoster(genesisRoster).TeamId);

        var oldRandomActiveTeam = TeamId.New(); // the retired Guid.NewGuid() synthesis.

        Assert.NotEqual(inviteAdvertisedTeam, oldRandomActiveTeam);
        // The NEW path agrees instead.
        Assert.Equal(inviteAdvertisedTeam, GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam: null));
    }

    [Fact]
    public void Explicit_team_id_override_wins_on_all_paths()
    {
        var rootSeed = NewSeed();
        var configured = "7e57c0de-0000-0000-0000-00000000beef";
        var configuredTeam = TeamId.Parse(configured);

        // Override flows through the SAME helper on the bootstrap path...
        Assert.Equal(configuredTeam, GenesisTeamId.Resolve(rootSeed, configured, multiTeam: null));
        // ...and the roster genesis + invite anchor scope to the override too.
        var genesisRoster = BuildProductionGenesisRoster(rootSeed, configured);
        Assert.Equal(configuredTeam, new TeamId(genesisRoster.TeamId));
        Assert.Equal(configuredTeam, TeamId.Parse(TeamTrustAnchor.FromRoster(genesisRoster).TeamId));

        // The override is NOT the seed-derived value (it genuinely overrode the default).
        Assert.NotEqual(new Guid(rootSeed.AsSpan(0, 16).ToArray()), configuredTeam.Value);
    }

    /// <summary>
    /// earlier repository ticket #3450 — the multi-team branch activates TeamBootstraps[] directly and never consulted
    /// GenesisTeamId, so with multi-team ON and LocalNode:TeamId UNSET the node served one team while the
    /// signed roster genesis and the invite anchor were seeded under a different, seed-derived one. On such a
    /// host invitations can never resolve the acting party in the roster. Found on the deployed dogfood host,
    /// whose committed config takes exactly this branch.
    /// </summary>
    [Fact]
    public void Multi_team_bootstrap_becomes_the_genesis_when_TeamId_is_unset()
    {
        var rootSeed = NewSeed();
        var bootstrapTeam = Guid.Parse("ccab0115-604f-4cf8-8a99-7f39cb6961ce");
        var multiTeam = new MultiTeamOptions
        {
            Enabled = true,
            TeamBootstraps = { new TeamBootstrap { TeamId = bootstrapTeam, DisplayName = "Harborline" } },
        };

        var resolved = GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam);

        Assert.Equal(new TeamId(bootstrapTeam), resolved);
        // ...and it is genuinely NOT the seed-derived value, i.e. the divergence is what closed.
        Assert.NotEqual(GenesisTeamId.Derive(rootSeed), resolved);
    }

    /// <summary>
    /// The GATE, not just the fallback: a populated TeamBootstraps list with multi-team DISABLED must leave
    /// the seed-derived genesis alone. Every Harborline App install is this shape — node_supervisor.rs pins
    /// LocalNode__MultiTeam__Enabled=false — so a fallback that applied unconditionally would re-home the
    /// roster genesis of every single-team install on upgrade.
    /// </summary>
    [Fact]
    public void Multi_team_bootstrap_is_ignored_when_multi_team_is_disabled()
    {
        var rootSeed = NewSeed();
        var multiTeam = new MultiTeamOptions
        {
            Enabled = false,
            TeamBootstraps = { new TeamBootstrap { TeamId = Guid.NewGuid() } },
        };

        Assert.Equal(GenesisTeamId.Derive(rootSeed),
            GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam));
    }

    /// <summary>
    /// Precedence is configured &gt; bootstrap &gt; seed-derived. A node pinned to a provisioned team keeps its
    /// pin even when a bootstrap list is present — which is what makes this change a no-op for the dogfood
    /// host as it stands, and the documented escape hatch for anyone who deliberately accepted the divergence.
    /// </summary>
    [Fact]
    public void Configured_team_id_outranks_the_multi_team_bootstrap()
    {
        var rootSeed = NewSeed();
        var configured = "7e57c0de-0000-0000-0000-00000000beef";
        var multiTeam = new MultiTeamOptions
        {
            Enabled = true,
            TeamBootstraps = { new TeamBootstrap { TeamId = Guid.NewGuid() } },
        };

        Assert.Equal(TeamId.Parse(configured), GenesisTeamId.Resolve(rootSeed, configured, multiTeam));
    }

    /// <summary>
    /// An enabled-but-empty section must not throw or mint anything: it falls through to seed-derived.
    /// </summary>
    [Fact]
    public void Enabled_multi_team_with_no_bootstraps_falls_through_to_seed_derived()
    {
        var rootSeed = NewSeed();
        var multiTeam = new MultiTeamOptions { Enabled = true };

        Assert.Equal(GenesisTeamId.Derive(rootSeed),
            GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam));
    }

    [Fact]
    public void Single_user_genesis_is_self_consistent_and_restart_stable()
    {
        var rootSeed = NewSeed();

        // The genesis the node creates for itself is for THIS team — the founder is a current member of the
        // genesis-team roster, and re-deriving off the same seed reconstructs the byte-identical team id.
        var rosterA = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null);
        var rosterB = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null);

        Assert.Equal(rosterA.TeamId, rosterB.TeamId); // restart-stable team id.
        Assert.Equal(rosterA.GenesisPartyId, rosterB.GenesisPartyId);
        Assert.True(rosterA.Contains(rosterA.GenesisPartyId)); // the founder is a member of its own genesis team.
        Assert.Equal(GenesisTeamId.Resolve(rootSeed, null, multiTeam: null).Value, rosterA.TeamId);
    }

    [Fact]
    public async Task Bootstrap_activates_the_seed_derived_genesis_team_when_TeamId_unset()
    {
        // The in-proc bootstrap proof: drive MultiTeamBootstrapHostedService.StartAsync with NO LocalNode:TeamId
        // but WITH the GenesisTeamIdProvider the composition root registers, and assert the ACTIVATED team equals
        // the seed-derived genesis the invite advertises. Pre-fix this asserted a random Guid and would FAIL.
        var rootSeed = NewSeed();
        var resolvedGenesis = GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, multiTeam: null);

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();                 // real ITeamContextFactory + IActiveTeamAccessor.
        services.AddHarborlineTeamMembershipStore();       // real IMutableTeamRegistry (in-memory).
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(resolvedGenesis));
        // LocalNode:TeamId deliberately UNSET + MultiTeam disabled (the Harborline App single-office boot contract).
        var options = new LocalNodeOptions { TeamId = null };
        options.MultiTeam.Enabled = false;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var sp = services.BuildServiceProvider();
        var bootstrap = sp.GetRequiredService<MultiTeamBootstrapHostedService>();
        await bootstrap.StartAsync(CancellationToken.None);

        var active = sp.GetRequiredService<IActiveTeamAccessor>().Active;
        Assert.NotNull(active);
        Assert.Equal(resolvedGenesis, active!.TeamId);

        // The 3-way match closes: the daemon-active team == the team the invite advertises.
        var genesisRoster = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null);
        var inviteAdvertisedTeam = TeamId.Parse(TeamTrustAnchor.FromRoster(genesisRoster).TeamId);
        Assert.Equal(inviteAdvertisedTeam, active.TeamId);
    }

    /// <summary>
    /// THE COUPLING TEST for the multi-team branch (deep-review finding on the earlier repository ticket #3450 PR).
    /// </summary>
    /// <remarks>
    /// Every other test here either drives the bootstrap with multi-team DISABLED (the legacy branch) or
    /// exercises <see cref="GenesisTeamId.Resolve"/> in ISOLATION. Neither shape can catch the thing that
    /// actually breaks: <c>MultiTeamBootstrapHostedService</c> selects <c>TeamBootstraps[0]</c> ITSELF and never
    /// calls <see cref="GenesisTeamId.Resolve"/>, so the resolver and the bootstrap are two hand-maintained
    /// copies of one rule. They agree today because the resolver deliberately MIRRORS the bootstrap — not by
    /// construction. If the bootstrap ever sorts, dedupes or skips an entry, the genesis silently stops
    /// following the active team and earlier repository ticket #3450 returns in a new shape.
    /// <para>
    /// So this asserts the two AGAINST EACH OTHER rather than either against a literal. It is the only thing
    /// coupling them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Bootstrap_active_team_agrees_with_the_resolved_genesis_on_the_multi_team_branch()
    {
        var rootSeed = NewSeed();
        // Two bootstraps, deliberately: a single-element list cannot tell "picks element 0" apart from
        // "picks the only one", so it would pass even if the two sides disagreed about WHICH element wins.
        var first = Guid.Parse("ccab0115-0000-4000-8000-00000000f00d");
        var second = Guid.Parse("dd7e0000-0000-4000-8000-00000000beef");

        var options = new LocalNodeOptions { TeamId = null };
        options.MultiTeam.Enabled = true;
        options.MultiTeam.TeamBootstraps.Add(new TeamBootstrap { TeamId = first, DisplayName = "First" });
        options.MultiTeam.TeamBootstraps.Add(new TeamBootstrap { TeamId = second, DisplayName = "Second" });

        // The resolver's answer, taken through the SAME entry point the composition root uses.
        var resolvedGenesis = GenesisTeamId.Resolve(rootSeed, configuredTeamId: null, options.MultiTeam);

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(resolvedGenesis));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<MultiTeamBootstrapHostedService>().StartAsync(CancellationToken.None);

        var active = sp.GetRequiredService<IActiveTeamAccessor>().Active;
        Assert.NotNull(active);

        // The assertion that matters: what the daemon ACTIVATED is what the resolver called the genesis.
        Assert.Equal(resolvedGenesis, active!.TeamId);

        // And the 3-way match closes — the invite advertises that same team.
        var genesisRoster = BuildProductionGenesisRoster(rootSeed, configuredTeamId: null, options.MultiTeam);
        var inviteAdvertisedTeam = TeamId.Parse(TeamTrustAnchor.FromRoster(genesisRoster).TeamId);
        Assert.Equal(inviteAdvertisedTeam, active.TeamId);

        // Guard the guard: had either side picked element 1, the assertions above would still both pass if
        // they picked it CONSISTENTLY. Pin element 0 explicitly so a shared drift is caught too.
        Assert.Equal(new TeamId(first), active.TeamId);
    }

    [Fact]
    public async Task Bootstrap_honors_explicit_TeamId_override()
    {
        var rootSeed = NewSeed();
        var configured = "7e57c0de-0000-0000-0000-00000000beef";
        var configuredTeam = TeamId.Parse(configured);

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        // The composition root resolves the SAME helper, so the provider already carries the override.
        services.AddSingleton(new GenesisTeamIdProvider(GenesisTeamId.Resolve(rootSeed, configured, multiTeam: null)));
        var options = new LocalNodeOptions { TeamId = configured };
        options.MultiTeam.Enabled = false;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<MultiTeamBootstrapHostedService>()
            .StartAsync(CancellationToken.None);

        var active = sp.GetRequiredService<IActiveTeamAccessor>().Active;
        Assert.NotNull(active);
        Assert.Equal(configuredTeam, active!.TeamId);
    }

    [Fact]
    public async Task Bootstrap_refuses_a_pin_that_differs_from_the_first_multi_team_bootstrap()
    {
        var pinned = Guid.Parse("7e57c0de-0000-0000-0000-00000000beef");
        var configuredBootstrap = Guid.Parse("ccab0115-0000-4000-8000-00000000f00d");
        var options = new LocalNodeOptions { TeamId = pinned.ToString("D") };
        options.MultiTeam.Enabled = true;
        options.MultiTeam.TeamBootstraps.Add(new TeamBootstrap
        {
            TeamId = configuredBootstrap,
            DisplayName = "Configured bootstrap",
        });

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(new TeamId(pinned)));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var sp = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sp.GetRequiredService<MultiTeamBootstrapHostedService>().StartAsync(CancellationToken.None));

        Assert.Contains(pinned.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(configuredBootstrap.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(sp.GetRequiredService<IActiveTeamAccessor>().Active);
    }

    /// <summary>No-op store activator — the bootstrap calls ActivateAsync; this satisfies it without opening a
    /// real SQLCipher store (mirrors the BSideEnrollmentJoinE2ETests no-op).</summary>
    private sealed class NoopStoreActivator : ITeamStoreActivator
    {
        public ValueTask ActivateAsync(TeamId teamId, CancellationToken ct) => default;
    }
}
