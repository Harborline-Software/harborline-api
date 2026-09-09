using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RosterAdmissionGrantBackfillTests
{
    private static readonly Guid Team = Guid.Parse("29300000-0000-0000-0000-000000000003");
    private static readonly TenantId Tenant = new(Team.ToString("D"));
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public async Task Effective_permissions_equal_backfilled_grants_before_and_after_durable_restart()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var (before, _) = await SeedAsync(store, 7);
        Assert.Equal(7, await Backfill(store).RunAsync());
        await using var reopened = SearchTestStore.Reopen(store);
        await using var provider = GateProvider(reopened);
        var closure = provider.GetRequiredService<IAuthorizationClosureReader>();
        var gate = provider.GetRequiredService<AuthorizationGate>();
        var after = await new VerifiedTenantRosterReader(new RosterFactory(reopened), new Ed25519Verifier())
            .ReadAsync(Tenant, default);
        foreach (var member in before.Members)
        {
            var principal = new ActorId(member.PartyId);
            // Both roster shapes carry the same membership facts; grant authority is read by the gate.
            var expected = Data.Identity.EffectiveMemberPermissions.Read(before, member.PartyId, principal);
            var actual = Data.Identity.EffectiveMemberPermissions.Read(after, member.PartyId, principal);
            var gatePermissions = await gate.InstallRootPermissionsAsync(principal, Tenant, At.AddDays(1), default);
            var grants = await closure.UserPermissionsAsync(Tenant, principal, At.AddDays(1), default);
            var grantPermissions = PermissionSet.From(grants.Atoms.Where(a => a.Scope.Value == "/")
                .Select(a => a.Operation.Value));
            Assert.True(actual.Member);
            Assert.Equal(expected, actual);
            // The TEETH (fix 4, item 5): the backfilled grant carries the set the ADMISSION recorded, read from
            // the pre-backfill signed roster — not a gate reading compared against a closure reading over the
            // same store, which is equal (and empty) whether or not the backfill conferred anything.
            var admitted = before.PermissionsOf(member.PartyId) ?? PermissionSet.Empty;
            Assert.NotEmpty(admitted.Permissions);
            Assert.Equal(admitted, grantPermissions);
            Assert.Equal(gatePermissions, grantPermissions);
            if (member.PartyId != "founder") Assert.Null(after.PermissionsOf(member.PartyId));
        }
    }

    [Fact]
    public async Task A_synced_member_answers_from_its_grant_and_one_without_a_grant_is_denied()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 3);
        Assert.Equal(3, await Backfill(store).RunAsync());
        var grants = new NodeEfGrantStore(store.Factory);
        var grant = Assert.Single(await grants.FindByPrincipalAsync(Tenant, new ActorId("member-2")));
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(new ActorId("founder"), At,
            new GrantReason(GrantReasonCodes.RevocationReview)));
        await using var provider = GateProvider(store);
        var gate = provider.GetRequiredService<AuthorizationGate>();
        var roster = await new VerifiedTenantRosterReader(new RosterFactory(store), new Ed25519Verifier())
            .ReadAsync(Tenant, default);

        // Both are members of the reconstruction; the gate's grant reading is the whole difference between them.
        Assert.Null(roster.PermissionsOf("member-1"));
        Assert.Null(roster.PermissionsOf("member-2"));
        foreach (var (party, allowed) in new[] { ("member-1", true), ("member-2", false) })
        {
            var inputs = Data.Identity.EffectiveMemberPermissions.Read(roster, party, new ActorId(party));
            Assert.True(inputs.Member);
            Assert.Equal(allowed, (await gate.InstallRootPermissionsAsync(
                new ActorId(party), Tenant, At.AddDays(1), default)).Contains(TeamRolePermissions.RecordsRead));
        }
    }

    [Fact]
    public async Task Production_boot_hook_converts_grants_before_hydrating_the_roster()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var (_, signer) = await SeedAsync(store, 3);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationSigner>(signer);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(store.Factory);
        services.AddSingleton<IDbContextFactory<NodeLocalRosterDbContext>>(new RosterFactory(store));
        services.AddNodeRoster();
        services.AddNodeAuthorizationModel();
        services.AddSingleton<RosterAdmissionGrantBackfill>();
        services.AddSingleton<RosterSyncBootstrapHostedService>();
        await using var provider = services.BuildServiceProvider();
        var boot = provider.GetRequiredService<RosterSyncBootstrapHostedService>();
        await boot.StartAsync(CancellationToken.None);
        await boot.StopAsync(CancellationToken.None);
        await using var db = store.CreateContext();
        Assert.Equal(3, await db.Grants.CountAsync());
        Assert.Equal(3, provider.GetRequiredService<RosterCrdtProjection>().Count);
    }

    [Fact]
    public async Task Concurrent_boot_attempts_commit_one_complete_conversion()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 3);
        var results = await Task.WhenAll(Task.Run(() => Backfill(store).RunAsync()),
            Task.Run(() => Backfill(store).RunAsync()));
        Assert.Equal(new[] { 0, 3 }, results.Order().ToArray());
        await using var db = store.CreateContext();
        Assert.Equal(3, await db.Grants.CountAsync());
        Assert.Single(await db.Set<RosterAdmissionGrantBackfillRow>().ToArrayAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task N_signed_admissions_become_N_grants_once_and_survive_restart(int count)
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, count);
        var log = new RecordingLogger();
        Assert.Equal(count, await Backfill(store, log).RunAsync());
        Assert.Equal([count], log.Counts);
        await using var reopened = SearchTestStore.Reopen(store);
        Assert.Equal(0, await Backfill(reopened, log).RunAsync());
        Assert.Equal([count], log.Counts);
        await using var db = reopened.CreateContext();
        Assert.Equal(count, await db.Grants.CountAsync());
        Assert.Equal(count, (await db.Set<RosterAdmissionGrantBackfillRow>().SingleAsync()).RecordCount);
        Assert.Contains("20260908030000_RosterAdmissionGrantBackfill", await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await using var provider = GateProvider(reopened);
        for (var i = 0; i < count; i++)
        {
            var principal = i == 0 ? "founder" : "member-" + i;
            var decision = await DecideAsync(provider, principal, "records:read");
            Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
            Assert.Single(await new NodeEfGrantStore(reopened.Factory).FindByPrincipalAsync(Tenant, new ActorId(principal)));
            if (i > 0) Assert.Equal(AuthorizationVerdict.Denied,
                (await DecideAsync(provider, principal, "records:write")).Verdict);
        }
    }

    [Fact]
    public async Task Backfill_does_not_recreate_a_revoked_grant_or_read_a_planted_record_set()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 2);
        Assert.Equal(2, await Backfill(store).RunAsync());
        var grants = new NodeEfGrantStore(store.Factory);
        var grant = Assert.Single(await grants.FindByPrincipalAsync(Tenant, new ActorId("member-1")));
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(new ActorId("founder"), At,
            new GrantReason(GrantReasonCodes.RevocationReview)));
        await using (var roster = store.CreateRosterContext())
        {
            (await roster.RosterRecords.SingleAsync(row => row.PartyId == "member-1")).MintingSessionEvidence = "tampered";
            await roster.SaveChangesAsync();
        }
        await using var reopened = SearchTestStore.Reopen(store);
        Assert.Equal(0, await Backfill(reopened).RunAsync());
        await using var provider = GateProvider(reopened);
        Assert.Equal(AuthorizationVerdict.Denied, (await DecideAsync(provider, "member-1", "records:read")).Verdict);
        Assert.Equal(AuthorizationVerdict.Denied, (await DecideAsync(provider, "member-1", "records:write")).Verdict);
        Assert.Equal(GrantStatus.Revoked, (await new NodeEfGrantStore(reopened.Factory).FindAsync(Tenant, grant.GrantId))!.Status);
    }

    [Fact]
    public async Task Existing_signed_revocation_is_preserved_in_the_migrated_grant()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var (roster, signer) = await SeedAsync(store, 2);
        var revoked = roster.SignRevoke("founder", signer, "member-1", new Ed25519Verifier(), At.AddSeconds(1), Guid.NewGuid());
        await using (var db = store.CreateRosterContext())
        {
            db.RosterRecords.Add(NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromRevocation(revoked.Signed)
                .AttestReceipt(signer, "founder", revoked.Signed.Signed.IssuedAt)));
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await Backfill(store).RunAsync());
        var grant = Assert.Single(await new NodeEfGrantStore(store.Factory).FindByPrincipalAsync(Tenant, new ActorId("member-1")));
        Assert.Equal(GrantStatus.Revoked, grant.Status);
        Assert.Equal(revoked.Signed.Signed.IssuedAt, grant.Revocation!.RevokedAt);
        await using var provider = GateProvider(store);
        Assert.Equal(AuthorizationVerdict.Denied, (await DecideAsync(provider, "member-1", "records:read")).Verdict);
    }

    [Fact]
    public async Task Tampered_admission_cannot_be_promoted_into_grant_authority()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 2);
        await using (var db = store.CreateRosterContext())
        {
            (await db.RosterRecords.SingleAsync(row => row.PartyId == "member-1")).MintingSessionEvidence = "tampered";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<VerifiedTenantRosterRefusedException>(() => Backfill(store).RunAsync());
        await using var grants = store.CreateContext();
        Assert.Empty(await grants.Grants.ToArrayAsync());
        Assert.Empty(await grants.Set<RosterAdmissionGrantBackfillRow>().ToArrayAsync());
    }

    [Fact]
    public async Task Historical_atom_publication_requires_definition_validation_and_rolls_back_every_row()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var signer = new Ed25519Signer(KeyPair.Generate());
        var permissions = PermissionCompositions.Owner.With("migration:unknown");
        var signature = RosterSigning.SignAdmission(signer, Team, "founder", signer.IssuerId,
            "founder", true, At, Guid.NewGuid());
        var admission = new MemberAdmissionRecord(Tenant.Value, "founder", signer.IssuerId, signature);
        await using (var roster = store.CreateRosterContext())
        {
            await roster.Database.MigrateAsync();
            var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(admission)
                .AttestReceipt(signer, "founder", admission.Admission.IssuedAt));
            row.SignedPermissionsJson = System.Text.Json.JsonSerializer.Serialize(permissions.Permissions.ToArray());
            roster.RosterRecords.Add(row);
            await roster.SaveChangesAsync();
        }
        var verified = await new VerifiedTenantRosterReader(new RosterFactory(store), new Ed25519Verifier()).ReadAsync(Tenant, CancellationToken.None);
        Assert.Single(verified.EnumerateAdmissions());
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Backfill(store).RunAsync());
        Assert.Contains("code operation catalogue", refusal.Message);
        await using var db = store.CreateContext();
        Assert.Empty(await db.Grants.ToArrayAsync());
        Assert.Empty(await db.AuthorizationRoles.ToArrayAsync());
        Assert.Empty(await db.AuthorizationDefinitions.ToArrayAsync());
        Assert.Empty(await db.AuthorizationOfferedRoles.ToArrayAsync());
        Assert.Empty(await db.Set<RosterAdmissionGrantBackfillRow>().ToArrayAsync());
    }

    /// <summary>
    /// 293 slice 4 fix 3 — a LIVE admission confers the admitted member's grant through the same derivation
    /// the 3a boot backfill runs over history, so the gate answers for a member the backfill never saw. The
    /// grant is keyed on the ROSTER PARTY ID, which is the actor id the gate is asked about on the roster
    /// plane; the assertion reads the grant store, never the conferral's own return value.
    /// </summary>
    [Fact]
    public async Task A_live_admission_confers_the_admitted_members_grant_the_gate_then_answers_from()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 1);
        var configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, new InMemoryRoleVocabulary());
        await using (var db = store.CreateContext()) await db.Database.MigrateAsync();

        await using var provider = GateProvider(store);
        Assert.Equal(AuthorizationVerdict.Denied, (await DecideAsync(provider, "late-joiner", "records:read")).Verdict);

        await configuration.ConferAdmissionGrantAsync(
            Tenant, "late-joiner", "founder", PermissionSet.Of("records:read"), At, default);

        var conferred = Assert.Single(
            await new NodeEfGrantStore(store.Factory).FindByPrincipalAsync(Tenant, new ActorId("late-joiner")));
        Assert.Equal(GrantStatus.Active, conferred.Status);
        Assert.Equal("/", conferred.Scope.Value);
        await using var admitted = GateProvider(store);
        Assert.Equal(AuthorizationVerdict.Allowed, (await DecideAsync(admitted, "late-joiner", "records:read")).Verdict);
        Assert.Equal(AuthorizationVerdict.Denied, (await DecideAsync(admitted, "late-joiner", "records:write")).Verdict);
    }

    /// <summary>
    /// The conferral is idempotent on (tenant, admitted party): a re-admission never mints a rival grant, so
    /// the admission path can be retried without doubling authority.
    /// </summary>
    [Fact]
    public async Task A_repeated_admission_confers_no_second_grant()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 1);
        var configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, new InMemoryRoleVocabulary());
        await using (var db = store.CreateContext()) await db.Database.MigrateAsync();
        var permissions = PermissionSet.Of("records:read");

        Assert.NotNull(await configuration.ConferAdmissionGrantAsync(Tenant, "twice", "founder", permissions, At, default));
        Assert.Null(await configuration.ConferAdmissionGrantAsync(Tenant, "twice", "founder", permissions, At, default));

        Assert.Single(await new NodeEfGrantStore(store.Factory).FindByPrincipalAsync(Tenant, new ActorId("twice")));
    }

    /// <summary>
    /// A conferral that cannot be written fails the admission and leaves NOTHING behind — no grant, no role,
    /// no capability definition. An operation outside the reviewed catalogue is the cheapest such failure;
    /// the admission's caller sees the throw and never publishes the roster record it was guarding.
    /// </summary>
    [Fact]
    public async Task A_failed_conferral_leaves_no_grant_and_no_definition_behind()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedAsync(store, 1);
        var configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, new InMemoryRoleVocabulary());
        await using (var db = store.CreateContext()) await db.Database.MigrateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => configuration.ConferAdmissionGrantAsync(
            Tenant, "refused", "founder", PermissionSet.Of("records:read").With("migration:unknown"), At, default));

        await using var grants = store.CreateContext();
        Assert.Empty(await grants.Grants.Where(row => row.SubjectId == "refused").ToArrayAsync());
        Assert.Empty(await grants.AuthorizationRoles.ToArrayAsync());
        Assert.Empty(await grants.AuthorizationDefinitions.ToArrayAsync());
    }

    private static async Task<(MemberRoster Roster, Ed25519Signer Signer)> SeedAsync(SearchTestStore store, int count)
    {
        var signer = new Ed25519Signer(KeyPair.Generate());
        var roster = MemberRoster.Genesis(Team, "founder", signer, new Ed25519Verifier(), At, Guid.NewGuid());
        for (var i = 1; i < count; i++) roster = roster.Admit("founder", signer, "member-" + i,
            KeyPair.Generate().PrincipalId, PermissionSet.Of("records:read"), new Ed25519Verifier(), At, Guid.NewGuid());
        await using var db = store.CreateRosterContext();
        await db.Database.MigrateAsync();
        // A pre-wire-version-3 install: the durable rows still carry the signed atoms each member was
        // admitted with. That column is what the one-time boot backfill converts into grants.
        db.RosterRecords.AddRange(roster.EnumerateAdmissions()
            .Select(record =>
            {
                var row = NodeRosterRecord.FromCrdtState(RosterRecordCrdtState.FromAdmission(record)
                    .AttestReceipt(signer, "founder", record.Admission.IssuedAt));
                row.SignedPermissionsJson = System.Text.Json.JsonSerializer.Serialize(
                    (roster.PermissionsOf(record.PartyId) ?? PermissionSet.Empty).Permissions.ToArray());
                return row;
            }));
        await db.SaveChangesAsync();
        return (roster, signer);
    }

    private static RosterAdmissionGrantBackfill Backfill(SearchTestStore store, RecordingLogger? logger = null) =>
        new(new RosterFactory(store), new NodeEfAuthorizationConfigurationStore(store.Factory, new InMemoryRoleVocabulary()), new Ed25519Verifier(), logger ?? new RecordingLogger());

    private static ServiceProvider GateProvider(SearchTestStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestKernelClock();
        services.AddSingleton(store.Factory);
        AuthorizationAdminRouteTests.RegisterGate(services);
        return services.BuildServiceProvider();
    }

    private static ValueTask<AuthorizationDecision> DecideAsync(ServiceProvider provider, string principal, string operation) =>
        provider.GetRequiredService<AuthorizationGate>().DecideAsync(
            new AuthorizationWriteContext(new ActorId(principal), Tenant, At.AddDays(1))
                .Request(AuthorizationOperation.Parse(operation), "record", "fixture"));

    private sealed class RosterFactory(SearchTestStore store) : IDbContextFactory<NodeLocalRosterDbContext>
    {
        public NodeLocalRosterDbContext CreateDbContext() => store.CreateRosterContext();
    }

    private sealed class RecordingLogger : ILogger<RosterAdmissionGrantBackfill>
    {
        internal List<int> Counts { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Information, level);
            Counts.Add(Assert.IsType<int>(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(state)
                .Single(pair => pair.Key == "Count").Value));
        }
    }
}
