using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// ADR 0066 clause 9 end-to-end: <see cref="MultiTeamBootstrapHostedService"/> establishes administrative
/// authority ONCE and PROJECTS it on every later boot.
/// </summary>
/// <remarks>
/// The pre-fix service called <c>AddMembershipAsync</c> unconditionally on every <c>StartAsync</c> with an
/// Admin permission set, no <c>MemberPublicKey</c> and no <c>AdmissionSignature</c>. Against that tree
/// <see cref="Second_boot_writes_no_new_authority"/> fails (two identical re-mints, no durable record to
/// count), <see cref="First_boot_establishes_a_signed_administrator"/> fails (both roster fields are null),
/// and <see cref="A_revoked_administrator_is_not_re_minted_by_the_next_boot"/> fails outright — the restart
/// re-granted the revoked administrator, which is the defect ADR 0066 names as the actual master key.
/// </remarks>
public sealed class BootstrapEstablishesOnceTests : IAsyncLifetime
{
    private string _directory = string.Empty;
    private ServiceProvider? _storeProvider;
    private IDbContextFactory<NodeLocalRosterDbContext> _contexts = null!;
    private byte[] _rootSeed = [];
    private readonly List<NodePrincipalSigner> _signers = [];
    private NodePrincipalSigner? _node;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _rootSeed = RandomNumberGenerator.GetBytes(32);
        _directory = Path.Combine(
            Path.GetTempPath(), "harborline-bootstrap-once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_directory, "roster.db")};Pooling=False"));
        _storeProvider = services.BuildServiceProvider();
        _contexts = _storeProvider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using var context = await _contexts.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var signer in _signers)
        {
            signer.Dispose();
        }

        if (_storeProvider is not null)
        {
            await _storeProvider.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A held SQLite handle on Windows is not a test failure.
        }
    }

    /// <summary>
    /// One "boot": a fresh service provider over the SAME durable store and the SAME root seed, exactly as a
    /// restart would be. Everything process-local (the membership registry, the roster) is rebuilt.
    /// </summary>
    private async Task<BootResult> BootAsync(MemberRoster? rosterState = null)
    {
        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);
        var signer = NodeSigner();
        var partyId = LocalPartyId();
        var roster = new NodeTeamRoster(rosterState ?? GenesisRoster());

        var services = new ServiceCollection();
        services.AddSingleton(signer.Signer);

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(genesisTeam));
        services.AddSingleton(roster);
        // The desktop plane's closure half of the one reading. A party the roster carries live never reaches
        // it (the roster edge answers), so it stands in for a composition that has one, unstubbed.
        services.AddDbContextFactory<NodeLocalSearchDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_directory, "authorization.db")};Pooling=False"));
        services.AddNodeAuthorizationModel();
        services.AddSingleton(new NodeAdministratorAuthority(
            _contexts, TimeProvider.System, TestAuthorization.AllowGate()));
        var options = new LocalNodeOptions { TeamId = null, DataDirectory = _directory };
        options.MultiTeam.Enabled = false;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        var provider = services.BuildServiceProvider();
        var searchFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
        await using (var search = await searchFactory.CreateDbContextAsync())
            await search.Database.EnsureCreatedAsync();
        // Ticket 293 slice 4 — this node's authority is a CONFERRED GRANT, not a roster permission set.
        // The founder's admission is what confers it in production, so the fixture calls the very same
        // derivation (NodeEfAuthorizationConfigurationStore.ConferAdmissionGrantAsync) rather than seeding
        // rows by hand. The roster edge still says member-or-ejected; the grant says what may be done.
        // Only a node the signed roster carries live holds one — an install that is not a live member
        // must reach the gate with nothing, which is the fresh-install leg's whole property.
        if (roster.Current.Contains(partyId))
        {
            await new NodeEfAuthorizationConfigurationStore(
                    searchFactory, new InMemoryRoleVocabulary([]))
                .ConferAdmissionGrantAsync(
                    ActiveTeamTenantContext.ProjectTenantId(genesisTeam),
                    BootResult.Operator.Value,
                    BootResult.Operator.Value,
                    PermissionCompositions.Owner,
                    DateTimeOffset.UnixEpoch);
        }
        await provider.GetRequiredService<MultiTeamBootstrapHostedService>()
            .StartAsync(CancellationToken.None);
        return new BootResult(provider, partyId, genesisTeam);
    }


    /// <summary>The node's canonical signer — ONE per test, kept alive for the whole test (the boot resolves
    /// its own live roster member by this key, so a disposed keypair would be a different failure).</summary>
    private NodePrincipalSigner NodeSigner() => _node ??= Track(new NodePrincipalSigner(_rootSeed));

    /// <summary>Some OTHER party's signer — a second device, or the successor a handover admits.</summary>
    private NodePrincipalSigner ForeignSigner() => Track(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));

    private NodePrincipalSigner Track(NodePrincipalSigner signer)
    {
        _signers.Add(signer);
        return signer;
    }

    /// <summary>This node's party id, derived exactly as the composition root derives it.</summary>
    private string LocalPartyId() =>
        $"os:test#{Convert.ToHexString(NodeSigner().Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant()}";

    /// <summary>The install's genesis roster: this node's key self-admitted as the founder.</summary>
    private MemberRoster GenesisRoster() => MemberRoster.StableGenesis(
        teamId: GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null).Value,
        founderPartyId: LocalPartyId(),
        founderSigner: NodeSigner().Signer,
        verifier: new Ed25519Verifier());

    /// <summary>
    /// Every event in the log EXCEPT the installer-window anchor, which is bookkeeping about the window
    /// rather than a statement about anyone's authority. Counting authority events is what these tests are
    /// about; the anchor has its own test.
    /// </summary>
    private async Task<int> AuthorityEventCountAsync()
    {
        await using var context = await _contexts.CreateDbContextAsync();
        return await context.AdministratorAuthority
            .CountAsync(record => record.Event != AdministratorAuthorityEvent.InstallerWindowOpened);
    }

    [Fact]
    public async Task Bootstrap_projects_permissions_without_placing_them_in_the_admission_signature()
    {
        await using var boot = await BootAsync();
        var membership = Assert.Single(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.Equal(PermissionCompositions.ForRole(TeamRole.Admin), membership.Permissions);
        Assert.NotNull(membership.AdmissionSignature);
        Assert.DoesNotContain(typeof(AdmissionSignature).GetProperties(), property => property.Name == "Permissions");
    }

    [Fact]
    public async Task First_boot_establishes_a_signed_administrator()
    {
        await using var boot = await BootAsync();

        var membership = Assert.Single(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.Equal(TeamRole.Admin, membership.Role);
        // The two fields the pre-fix mint left null — ADR 0066 clause 3's evidence of admission, taken off
        // the signed genesis self-admission rather than synthesized.
        Assert.False(string.IsNullOrEmpty(membership.MemberPublicKey));
        Assert.NotNull(membership.AdmissionSignature);
        Assert.True(membership.AdmissionSignature!.IsGenesis);

        await using var context = await _contexts.CreateDbContextAsync();
        var established = Assert.Single(await context.AdministratorAuthority.AsNoTracking()
            .Where(record => record.Event == AdministratorAuthorityEvent.Established)
            .ToArrayAsync());
        Assert.Equal(AdministratorProvenance.Bootstrap, established.Provenance);
        Assert.Equal(boot.PartyId, established.PartyId);
    }

    [Fact]
    public async Task Second_boot_writes_no_new_authority()
    {
        await using (var first = await BootAsync())
        {
            Assert.Single(await first.Memberships.GetMembershipsAsync(BootResult.Operator));
        }

        Assert.Equal(1, await AuthorityEventCountAsync());

        await using var second = await BootAsync();

        // Still exactly one event: the boot PROJECTED, it did not establish.
        Assert.Equal(1, await AuthorityEventCountAsync());
        // And the node is still usable — projection is what keeps the removal of the mint from bricking it.
        var membership = Assert.Single(await second.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.Equal(TeamRole.Admin, membership.Role);
    }

    [Fact]
    public async Task A_revoked_administrator_is_not_re_minted_by_the_next_boot()
    {
        // THE defect, stated as a test. Pre-fix, revoking an administrator did not stick: the next restart
        // re-upserted them as full Admin. Here the revocation survives the restart, and the boot refuses to
        // establish because the installer is sealed.
        //
        // THE ASSERTION THAT WAS MISSING. This test used to check only the durable log, which is the half
        // that was already correct — so it stayed green while the boot went on granting the OS-user actor
        // full Admin from SOMEBODY ELSE'S record. What matters to a caller is the MEMBERSHIP, so that is
        // what is asserted: after the revocation, the operator holds no administrative membership at all.
        BootResult first;
        await using ((first = await BootAsync()).ConfigureAwait(false))
        {
            Assert.Single(await first.Memberships.GetMembershipsAsync(BootResult.Operator));
        }

        var authority = new NodeAdministratorAuthority(
            _contexts, TimeProvider.System, TestAuthorization.AllowGate());
        // A second administrator IN THE SAME TENANT, so the last-usable-administrator invariant permits the
        // revocation — and so the next boot has another usable record it could wrongly project.
        await SeedSecondAdministratorAsync(first.TeamId.Value.ToString("D"), "os:second#bbbb");
        var revoked = await authority.AppendRemovalAsync(
            first.TeamId.Value.ToString("D"),
            first.PartyId,
            AdministratorAuthorityEvent.Revoked,
            "test");
        Assert.True(revoked.Applied);

        await using var second = await BootAsync();

        // The boot appended nothing: establishment (1) + seed (2) + revocation (3), and no fourth event.
        Assert.Equal(3, await AuthorityEventCountAsync());
        var stillUsable = await authority.UsableAsync();
        Assert.DoesNotContain(stillUsable, administrator =>
            string.Equals(administrator.PartyId, first.PartyId, StringComparison.Ordinal));

        // And the authority the node actually ENFORCES on is gone too. The other party's record is still
        // usable in this tenant, and it is NOT this operator's, so nothing is projected onto this actor.
        Assert.Empty(await second.Memberships.GetMembershipsAsync(BootResult.Operator));
    }

    [Fact]
    public async Task A_converged_revocation_of_the_founder_is_not_projected_after_a_restart()
    {
        // Ticket 290 acceptance 2 and 3. Revocation drops the founder from LIVE roster state but deliberately
        // keeps the genesis party id — so a projection keyed on that id re-armed the revoked founder with full
        // desktop-plane authority on the very next restart. The administrator-authority log is left UNTOUCHED
        // here on purpose: this asserts the projection refuses on live membership alone.
        BootResult first;
        await using ((first = await BootAsync()).ConfigureAwait(false))
        {
            Assert.Single(await first.Memberships.GetMembershipsAsync(BootResult.Operator));
        }

        // The canonical flow: hand ownership over, then revoke the founder (the no-bricking floor requires the
        // successor to exist first). This is also the shape a peer's converged revocation adopts.
        var successor = ForeignSigner();
        const string SuccessorParty = "os:successor#eeee";
        var revoked = GenesisRoster()
            .Admit(
                admitterPartyId: first.PartyId,
                admitterSigner: NodeSigner().Signer,
                newPartyId: SuccessorParty,
                newPublicKey: successor.Signer.IssuerId,
                grantedPermissions: PermissionCompositions.Owner,
                verifier: new Ed25519Verifier(),
                issuedAt: DateTimeOffset.UnixEpoch,
                nonce: Guid.Parse("29000000-0000-4000-8000-000000000002"))
            .Revoke(SuccessorParty, first.PartyId);
        Assert.False(revoked.Contains(first.PartyId));
        Assert.Equal(first.PartyId, revoked.GenesisPartyId); // the id the old projection matched on, unchanged.

        await using var second = await BootAsync(revoked);

        Assert.Empty(await second.Memberships.GetMembershipsAsync(BootResult.Operator));
        // Acceptance 2, read through the desktop plane's own surface — the one that answers from the registry.
        using var capture = new Harborline.Api.LocalNodeHost.Tests.Authorization.RosterDecisionCapture();
        Assert.False(second.DesktopPlane.HasPermission(Permission.GrantPermissions));
        var evidence = capture.AssertSingle(false);
        Assert.True(evidence.Roster!.Ejected);
        Assert.False(evidence.Roster.RegistryMember);
        // And the log still folds the founder usable: it is LIVE MEMBERSHIP that refused, nothing else.
        var authority = new NodeAdministratorAuthority(
            _contexts, TimeProvider.System, TestAuthorization.AllowGate());
        Assert.Contains(await authority.UsableAsync(), administrator =>
            string.Equals(administrator.PartyId, first.PartyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_live_successor_is_projected_even_though_it_is_not_the_genesis_party()
    {
        // Ticket 290 acceptance 4 — the fix must not strand an install that handed over. Here the founder is
        // ANOTHER device's party and this node's key is a live admitted member, so the party to project is
        // found by membership; a genesis admission is just the first admission.
        var founder = ForeignSigner();
        const string FounderParty = "os:founder#aaaa";
        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);
        var verifier = new Ed25519Verifier();
        var adopted = MemberRoster
            .StableGenesis(genesisTeam.Value, FounderParty, founder.Signer, verifier)
            .Admit(
                admitterPartyId: FounderParty,
                admitterSigner: founder.Signer,
                newPartyId: LocalPartyId(),
                newPublicKey: NodeSigner().Signer.IssuerId,
                grantedPermissions: PermissionCompositions.Owner,
                verifier: verifier,
                issuedAt: DateTimeOffset.UnixEpoch,
                nonce: Guid.Parse("29000000-0000-4000-8000-000000000004"));

        // The successor's own durable establishment — the boot PROJECTS it, it does not establish anything.
        await SeedSecondAdministratorAsync(genesisTeam.Value.ToString("D"), LocalPartyId());

        await using var boot = await BootAsync(adopted);

        var membership = Assert.Single(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.Equal(TeamRole.Admin, membership.Role);
        // The projection composes the Admin role's set (grant:permissions is the OWNER holding, seeded as a
        // grant elsewhere) — what matters here is that the successor is projected at all.
        Assert.NotNull(boot.DesktopPlane.Roles.Single());
        Assert.Equal(1, await AuthorityEventCountAsync());
    }

    [Fact]
    public async Task A_fresh_install_that_is_not_a_live_member_establishes_nobody()
    {
        // Ticket 290 acceptance 4, the INSTALLER leg. A node that joined an existing tenant boots with an
        // empty administrator log, and the roster's genesis is another device's party. Taking the installer
        // candidate from the genesis party id would burn ADR 0066 clause 4's one-shot on a party this node
        // is NOT — permanently audited, and the node could never establish its own afterwards. The candidate
        // comes from the same live-membership reading the projection uses, so this node establishes nothing.
        // (The legitimate fresh install, where the node's own key IS the live founder, still establishes:
        // First_boot_establishes_a_signed_administrator.)
        var founder = ForeignSigner();
        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);
        var foreign = MemberRoster.StableGenesis(
            genesisTeam.Value, "os:founder#aaaa", founder.Signer, new Ed25519Verifier());
        Assert.Equal(0, await AuthorityEventCountAsync()); // a fresh install: the installer window is open.

        await using var boot = await BootAsync(foreign);

        Assert.Equal(0, await AuthorityEventCountAsync());
        Assert.Empty(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.False(boot.DesktopPlane.HasPermission(Permission.GrantPermissions));
        // Refusing to establish must not refuse to start.
        Assert.NotNull(boot.ActiveTenant);
    }

    [Fact]
    public async Task Another_partys_authority_is_never_projected_onto_the_operator()
    {
        // The two-party case, stated on its own. Selecting the record to project by TENANT alone and then
        // granting to the compile-time NodeOperator constant meant party P2's authority became P1's local
        // Admin on the next boot — so revoking P1 changed the durable log and changed NOTHING about what
        // the node enforced. The record's OWN party id has to decide who is granted.
        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);

        // A tenant that has a usable administrator — one this node's operator is NOT.
        await SeedSecondAdministratorAsync(genesisTeam.Value.ToString("D"), "os:somebody-else#ffff");

        await using var boot = await BootAsync();

        // The installer is gated (the tenant already has an administrator), and the existing administrator
        // belongs to another party, so the operator holds nothing.
        Assert.Empty(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.Equal(1, await AuthorityEventCountAsync());
        // The node still booted with an active tenant: refusing to grant must not refuse to start.
        Assert.NotNull(boot.ActiveTenant);
    }

    [Fact]
    public async Task A_broken_hash_chain_stops_the_boot_projecting_anything()
    {
        // A row inserted by direct SQL used to fold in as a usable administrator and be projected as full
        // Admin, because ComputeHash had exactly one caller — the writer. The chain is now verified before
        // anything is granted, and granting nothing is the safe side of that decision.
        await using (var first = await BootAsync())
        {
            Assert.Single(await first.Memberships.GetMembershipsAsync(BootResult.Operator));
        }

        await using (var context = await _contexts.CreateDbContextAsync())
        {
            var established = await context.AdministratorAuthority
                .SingleAsync(record => record.Event == AdministratorAuthorityEvent.Established);
            established.Reason = "edited-underneath-the-node";
            await context.SaveChangesAsync();
        }

        await using var second = await BootAsync();

        Assert.Empty(await second.Memberships.GetMembershipsAsync(BootResult.Operator));
    }

    [Fact]
    public async Task A_host_with_no_durable_authority_enrolls_nobody()
    {
        // Fail closed. "Mint an unsigned Admin when the durable authority cannot be consulted" is the defect
        // restated as a fallback, so the absence of the authority must produce NO membership, not a mint.
        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(genesisTeam));
        var options = new LocalNodeOptions { TeamId = null, DataDirectory = _directory };
        options.MultiTeam.Enabled = false;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MultiTeamBootstrapHostedService>()
            .StartAsync(CancellationToken.None);

        Assert.Empty(await provider.GetRequiredService<IMutableTeamRegistry>()
            .GetMembershipsAsync(BootResult.Operator));
        // The active team is still materialized: removing the mint must not stop the node booting.
        Assert.NotNull(provider.GetRequiredService<IActiveTeamAccessor>().Active);
    }

    [Fact]
    public async Task The_installer_window_closes_and_recovery_still_works()
    {
        // A node whose authority log has been sitting empty for longer than the bound cannot be
        // bootstrapped, and the offline recovery path is what keeps that from being a brick (ADR 0066
        // clauses 6 and 8). The anchor is a row in the log, written by the first boot that saw it empty —
        // NOT the data directory's creation timestamp, which anyone who can write that directory can move.
        await new NodeAdministratorAuthority(
                _contexts,
                new FixedClock(DateTimeOffset.UtcNow.AddDays(-30)),
                TestAuthorization.AllowGate())
            .ObserveEmptyLogAsync();

        var genesisTeam = GenesisTeamId.Resolve(_rootSeed, configuredTeamId: null, multiTeam: null);
        var roster = new NodeTeamRoster(GenesisRoster());

        var services = new ServiceCollection();

        services.AddSingleton(NodeSigner().Signer);
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddHarborlineKernelRuntime();
        services.AddHarborlineMultiTeam();
        services.AddHarborlineTeamMembershipStore();
        services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();
        services.AddSingleton(new GenesisTeamIdProvider(genesisTeam));
        services.AddSingleton(roster);
        var authority = new NodeAdministratorAuthority(
            _contexts, TimeProvider.System, TestAuthorization.AllowGate());
        services.AddSingleton(authority);
        var options = new LocalNodeOptions
        {
            TeamId = null,
            DataDirectory = _directory,
            InstallerWindow = TimeSpan.FromHours(24),
        };
        options.MultiTeam.Enabled = false;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<MultiTeamBootstrapHostedService>();

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MultiTeamBootstrapHostedService>()
            .StartAsync(CancellationToken.None);

        Assert.Equal(0, await AuthorityEventCountAsync());
        Assert.Empty(await provider.GetRequiredService<IMutableTeamRegistry>()
            .GetMembershipsAsync(BootResult.Operator));

        // Recovery is not window-bounded, so the node is recoverable rather than bricked.
        using var recovery = NodeAdministratorAuthority.NodeAdministratorOfflineRecoveryFactory.TryAcquire(
            _directory, out var recoveryFailure, createDirectory: false);
        Assert.NotNull(recovery);
        Assert.Equal(NodeRunLockFailure.None, recoveryFailure);
        var recovered = await recovery!.RecoverAsync(
            _contexts,
            TimeProvider.System,
            AdministratorRecoveryCommand.DeriveGenesisCandidate(_rootSeed, genesisTeam.Value));
        Assert.True(recovered.Applied);
        Assert.Equal(
            AdministratorProvenance.Recovery,
            Assert.Single(await authority.UsableAsync()).Provenance);
    }

    [Fact]
    public async Task A_converged_revocation_is_refused_by_the_desktop_plane_without_a_restart()
    {
        // Ticket 290 slice 3. The registry is refilled only at boot, so while it was the decider a founder
        // revoked mid-process kept full desktop authority until somebody restarted the node. The plane now
        // answers from live roster membership on every call, so the revocation lands on the same request.
        await using var boot = await BootAsync();
        using var capture = new Harborline.Api.LocalNodeHost.Tests.Authorization.RosterDecisionCapture();
        Assert.True(boot.DesktopPlane.HasPermission(Permission.GrantPermissions));
        Assert.True(capture.AssertSingle(true).Roster!.RegistryMember);
        capture.Evidence.Clear();

        var successor = ForeignSigner();
        const string SuccessorParty = "os:successor#dddd";
        boot.Roster.AdoptSyncedRoster(GenesisRoster()
            .Admit(
                admitterPartyId: boot.PartyId,
                admitterSigner: NodeSigner().Signer,
                newPartyId: SuccessorParty,
                newPublicKey: successor.Signer.IssuerId,
                grantedPermissions: PermissionCompositions.Owner,
                verifier: new Ed25519Verifier(),
                issuedAt: DateTimeOffset.UnixEpoch,
                nonce: Guid.Parse("29000000-0000-4000-8000-000000000003"))
            .Revoke(SuccessorParty, boot.PartyId));

        // The cache is deliberately left stale: this is the boot's projection, and nothing re-ran it.
        Assert.Single(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));
        Assert.False(boot.DesktopPlane.HasPermission(Permission.GrantPermissions));
        Assert.True(capture.AssertSingle(false).Roster!.Ejected);
        capture.Evidence.Clear();
        Assert.Empty(boot.DesktopPlane.Roles);
        // Ticket 293 slice 4 — the candidate acts a role enumeration asks about come from the party's
        // GRANTS now, not from the roster edge, so an ejected founder produces one refusal per candidate
        // rather than a single one. Every candidate must be refused, and each one still carries the
        // registry evidence: it is live membership that refused, nothing else.
        Assert.NotEmpty(capture.Evidence);
        Assert.All(capture.Evidence, evidence =>
        {
            Assert.False(evidence.Allowed);
            Assert.True(evidence.Roster!.RegistryMember);
        });
    }

    [Fact]
    public async Task Both_planes_answer_the_same_for_a_set_of_acts_when_the_roster_edge_narrows()
    {
        // Ticket 290 slice 3 — parity. The boot projected this node as Admin into the registry; the signed
        // roster then narrows its edge. Every act must follow the roster edge on BOTH planes, which is only
        // true because the desktop plane reads EffectiveMemberPermissions rather than the cache.
        string[] acts =
        [
            Permission.GrantPermissions,
            TeamRolePermissions.MembersManage,
            TeamRolePermissions.LedgerPost,
            TeamRolePermissions.RecordsWrite,
            TeamRolePermissions.RecordsRead,
        ];

        await using var boot = await BootAsync();
        var cached = Assert.Single(await boot.Memberships.GetMembershipsAsync(BootResult.Operator));

        var successor = ForeignSigner();
        const string SuccessorParty = "os:successor#cccc";
        boot.Roster.AdoptSyncedRoster(GenesisRoster()
            .Admit(
                admitterPartyId: boot.PartyId,
                admitterSigner: NodeSigner().Signer,
                newPartyId: SuccessorParty,
                newPublicKey: successor.Signer.IssuerId,
                grantedPermissions: PermissionCompositions.Owner,
                verifier: new Ed25519Verifier(),
                issuedAt: DateTimeOffset.UnixEpoch,
                nonce: Guid.Parse("29000000-0000-4000-8000-000000000004"))
            // The founder narrows its OWN live edge to a plain member's set; the successor keeps the
            // root-grant, so the no-bricking floor is satisfied.
            .Grant(boot.PartyId, boot.PartyId, PermissionCompositions.Member));

        var webPlane = await boot.WebPlaneReadingAsync(boot.PartyId);
        Assert.NotNull(webPlane);
        var desktopPlane = boot.DesktopPlane;
        foreach (var act in acts)
        {
            using var capture = new Harborline.Api.LocalNodeHost.Tests.Authorization.RosterDecisionCapture();
            var expected = webPlane!.Contains(act);
            Assert.Equal(expected, desktopPlane.HasPermission(act));
            Assert.True(capture.AssertSingle(expected).Roster!.RegistryMember);
        }

        using (var capture = new Harborline.Api.LocalNodeHost.Tests.Authorization.RosterDecisionCapture())
        {
            Assert.Contains("Admin", desktopPlane.Roles);
            Assert.True(capture.AssertSingle(true).Roster!.RegistryMember);
        }

        // Not vacuous: the cache the boot wrote still says Admin, so at least one act would answer
        // differently if the registry were still the decider.
        Assert.Contains(acts, act =>
            cached.EffectivePermissions.Contains(act) != desktopPlane.HasPermission(act));
    }

    private async Task SeedSecondAdministratorAsync(string teamId, string partyId)
    {
        await using var context = await _contexts.CreateDbContextAsync();
        var tip = await context.AdministratorAuthority.AsNoTracking()
            .OrderByDescending(record => record.Sequence).FirstOrDefaultAsync();
        var record = new AdministratorAuthorityRecord
        {
            Sequence = (tip?.Sequence ?? 0) + 1,
            TeamId = teamId,
            PartyId = partyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = AdministratorProvenance.Recovery,
            MemberPublicKey = "cHVibGljLWtleQ",
            AdmissionSignature = "c2lnbmF0dXJl",
            AdmittedByPublicKey = "cHVibGljLWtleQ",
            AdmittedByPartyId = partyId,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Reason = "test-seed",
            PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash,
            Hash = string.Empty,
        };
        record.Hash = AdministratorAuthorityRecord.ComputeHash(record);
        context.AdministratorAuthority.Add(record);
        await context.SaveChangesAsync();
    }

    private sealed class BootResult(ServiceProvider provider, string partyId, TeamId teamId) : IAsyncDisposable
    {
        public static readonly ActorId Operator = new(ActiveTeamAuthorizationContext.LocalUserId);

        public IMutableTeamRegistry Memberships => provider.GetRequiredService<IMutableTeamRegistry>();

        public object? ActiveTenant => provider.GetRequiredService<IActiveTeamAccessor>().Active;

        /// <summary>
        /// The desktop plane's own authorization surface, composed as production composes it (ticket 290
        /// slice 3): the registry is carried as the boot's cache, and the signed roster + this node's signer
        /// + the gate are the one reading it actually answers from.
        /// </summary>
        public ActiveTeamAuthorizationContext DesktopPlane => new(
            provider.GetRequiredService<IActiveTeamAccessor>(),
            provider.GetRequiredService<IMutableTeamRegistry>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<NodeTeamRoster>(),
            provider.GetRequiredService<IOperationSigner>(),
            gate: provider.GetRequiredService<AuthorizationGate>());

        /// <summary>This boot's live roster holder — the plane a converged revocation lands on.</summary>
        public NodeTeamRoster Roster => provider.GetRequiredService<NodeTeamRoster>();

        /// <summary>The one reading, asked exactly as the web plane's PEP asks it.</summary>
        public async Task<PermissionSet?> WebPlaneReadingAsync(string partyId)
        {
            var tenant = ActiveTeamTenantContext.ProjectTenantId(teamId);
            var inputs = EffectiveMemberPermissions.Read(Roster.Current, partyId, Operator);
            var allowed = new List<string>();
            var permissions = await provider.GetRequiredService<AuthorizationGate>().InstallRootPermissionsAsync(
                Operator, tenant, DateTimeOffset.UnixEpoch, CancellationToken.None);
            foreach (var permission in permissions.Permissions)
            {
                var operation = AuthorizationOperation.Parse(permission);
                var decision = await provider.GetRequiredService<AuthorizationGate>().DecideAsync(
                    new AuthorizationWriteContext(Operator, tenant, DateTimeOffset.UnixEpoch)
                        .Request(operation, AuthorizationGate.RecordKindFor(operation), "session") with { Roster = inputs });
                if (decision.Verdict == AuthorizationVerdict.Allowed) allowed.Add(permission);
            }
            return allowed.Count == 0 ? null : PermissionSet.From(allowed);
        }

        public string PartyId => partyId;

        public TeamId TeamId => teamId;

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class NoopStoreActivator : ITeamStoreActivator
    {
        public ValueTask ActivateAsync(TeamId teamId, CancellationToken ct) => default;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
