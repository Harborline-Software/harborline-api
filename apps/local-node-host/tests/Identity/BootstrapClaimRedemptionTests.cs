using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class BootstrapClaimRedemptionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.FromString("bootstrap-tenant");
    private static readonly PrincipalUserId Founder = new("bootstrap-founder");
    private static readonly CanonicalPartyReference FounderParty = new("bootstrap-party");

    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Production", true, false)]
    [InlineData("Development", false, false)]
    [InlineData("Development", true, true)]
    public async Task Composed_Seed_Profile_Accepts_Exactly_Its_Installer_Set(
        string environmentName,
        bool persistedDevelopmentSet,
        bool expected)
    {
        var compositionDirectory = Path.Combine(
            Path.GetTempPath(), $"bootstrap-profile-{Guid.NewGuid():N}");
        var clock = new MutableClock(Now);
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        $"--environment={environmentName}",
                        "--LocalNode:RootSeedHex=" + new string('7', 64),
                        "--LocalNode:WebClient:Enabled=true",
                        "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                        "--Logging:EventLog:LogLevel:Default=None",
                    ],
                    sessionTokenOverride: "bootstrap-profile-session-token",
                    dataDirectory: compositionDirectory,
                    kernelClock: clock,
                    installFootprintRootOverride: compositionDirectory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        throw new CompositionProbeCompleteException();
                    }));

            Assert.NotNull(provider);
            var encryptionGuard = provider.GetServices<IHostedService>()
                .OfType<LocalNodeStoreEncryptionGuard>().Single();
            await encryptionGuard.StartAsync(CancellationToken.None);
            var created = await provider.GetRequiredService<InstallationFounderBootstrapService>()
                .InitializeAsync(InstallationFounderBootstrapServiceTests.Command(
                    "founder", InstallationFounderBootstrapCeremony.CorrelationId));
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>().InstallAsync(
                Tenant,
                Now,
                persistedDevelopmentSet
                    ? AuthorizationSeedProfile.Development
                    : AuthorizationSeedProfile.Production);
            var redemption = provider.GetRequiredService<BootstrapClaimRedemptionService>();
            Assert.Equal(expected,
                await redemption.IsSurfaceAvailableAsync(created.InstallationIdentityId!, Tenant));
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(compositionDirectory))
                Directory.Delete(compositionDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Founder_Redeems_If_And_Only_If_Existing_Grants_Are_The_Exact_Real_Seed_Set()
    {
        string[] cases =
        [
            "exact seed set",
            "lookalike ids",
            "one missing",
            "one duplicate",
            "one extra installer-kind row",
            "one extra non-installer row",
            "all-empty",
        ];

        foreach (var testCase in cases)
        {
            await using var harness = await Harness.CreateAsync(seedAuthorization: false);
            if (testCase != "all-empty")
            {
                await CopyRealAuthorizationSeedAsync(
                    harness,
                    omittedSource: testCase == "one missing"
                        ? AccessGrantAuthorizationSeed.DevWorkflowSeederGrantSource
                        : null,
                    replaceIds: testCase == "lookalike ids");
            }

            if (testCase == "one duplicate")
            {
                await ExecuteAsync(harness.DatabasePath,
                    """
                    DROP INDEX ux_test_grant_source;
                    INSERT INTO search_grants
                    SELECT 'aaaaaaaa-0000-0000-0000-000000000001', tenant_id, subject_id,
                        role_vocabulary, role_name, scope_type, scope_value, residency,
                        validity_from_unix_ms, validity_until_unix_ms, status, granter_kind,
                        granted_by, granted_at_unix_ms, source, reason_code, reason_reference,
                        approver, last_reviewed_at_unix_ms, last_reviewed_by, validity_changed_by,
                        validity_change_reason_code, validity_change_reason_reference, revoked_by,
                        revoked_at_unix_ms, revocation_reason_code, revocation_reason_reference,
                        source_reference, owner_version
                    FROM search_grants
                    WHERE source_reference = 'authorization-seed:system-scheduler';
                    """);
            }
            else if (testCase == "one extra installer-kind row")
            {
                await harness.GrantStore.AppendAsync(
                    Tenant,
                    SystemSeedGrant(
                        new GrantId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002")),
                        "sys.extra",
                        AccessGrantAuthorizationSeed.SchedulerRole,
                        "authorization-seed:extra"),
                    "authorization-seed:extra");
            }
            else if (testCase == "one extra non-installer row")
            {
                await harness.GrantStore.AppendAsync(
                    Tenant,
                    Grant(
                        new GrantId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003")),
                        GrantSourceKind.Manual,
                        GranterKind.Person,
                        "existing-admin"),
                    "existing-first-grant");
            }

            var expected = testCase == "exact seed set";
            Assert.Equal(expected,
                await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
            Assert.Equal(
                expected ? BootstrapClaimRedemptionStatus.Redeemed : BootstrapClaimRedemptionStatus.SurfaceUnavailable,
                (await harness.Service.RedeemAsync(
                    await harness.IssueClaimAsync("founder"), harness.Target)).Status);
        }
    }

    [Fact]
    public async Task Desktop_Issuer_Verifies_Interactive_Founder_And_Binds_Target_Window_And_Installation()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder");

        Assert.Equal(BootstrapClaimIssuerKind.DesktopOsSession, claim.IssuerKind);
        Assert.Equal("desktop-os-session:founder", claim.IssuerIdentity);
        Assert.Equal(harness.InstallationId, claim.InstallationId);
        Assert.Equal(Now, claim.IssuedAt);
        Assert.Equal(Now, claim.NotBefore);
        Assert.Equal(Now.AddMinutes(5), claim.ExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(claim.Nonce));

        var wrongFounder = new DesktopOsSessionBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, "somebody-else", new FixedDesktopEvidence("founder", true));
        var nonInteractive = new DesktopOsSessionBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, "founder", new FixedDesktopEvidence("founder", false));
        Assert.Null(await wrongFounder.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));
        Assert.Null(await nonInteractive.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Founder_Bootstrap_Accepts_A_Decomposed_Os_Account_Name_As_The_Composed_Actor()
    {
        // Ticket 274: an OS account name stored decomposed (mac: "jose" + U+0301) must bootstrap the
        // founder and land the SAME ActorId as the composed spelling, not throw at redemption.
        const string decomposed = "josé";
        const string composed = "josé";
        Assert.NotEqual(composed, decomposed);
        const string expectedIssuer = "desktop-os-session:" + composed;

        await using var decomposedHarness = await Harness.CreateAsync();
        var decomposedClaim = await decomposedHarness.IssueClaimAsync(decomposed);
        await using var composedHarness = await Harness.CreateAsync();
        var composedClaim = await composedHarness.IssueClaimAsync(composed);

        Assert.Equal(expectedIssuer, decomposedClaim.IssuerIdentity);
        Assert.Equal(composedClaim.IssuerIdentity, decomposedClaim.IssuerIdentity);

        var result = await decomposedHarness.Service.RedeemAsync(decomposedClaim, decomposedHarness.Target);

        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed, result.Status);
        var grant = Assert.Single(
            await decomposedHarness.GrantStore.SnapshotAsync(Tenant),
            item => item.Role == RoleReference.Administrator);
        Assert.Equal(expectedIssuer, grant.Grant.Approver.Value);
        Assert.Equal(new ActorId(expectedIssuer), grant.Grant.Approver);

        // The filesystem-owner issuer derives from the real process account; assert only that what it
        // stores is canonical (weak on an ASCII host, but it is the same minting line).
        var ownerClaim = Assert.IsType<BootstrapClaim>(
            await new SelfHostedFileSystemOwnerBootstrapClaimIssuer(
                    composedHarness.Clock,
                    composedHarness.IdentityFactory,
                    composedHarness.DatabasePath,
                    new FixedOwnerEvidence(true))
                .IssueAsync(composedHarness.Target, TimeSpan.FromMinutes(5)));
        Assert.True(ActorId.IsCanonical(ownerClaim.IssuerIdentity));
    }

    [Fact]
    public async Task File_Owner_And_Hosted_Issuers_Fail_Closed_Without_Verified_Noncaller_Evidence()
    {
        await using var harness = await Harness.CreateAsync();
        var deniedOwner = new SelfHostedFileSystemOwnerBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, harness.DatabasePath, new FixedOwnerEvidence(false));
        Assert.Null(await deniedOwner.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));
        var verifiedOwner = new SelfHostedFileSystemOwnerBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, harness.DatabasePath, new FixedOwnerEvidence(true));
        Assert.Equal(
            BootstrapClaimIssuerKind.SelfHostedFileSystemOwner,
            Assert.IsType<BootstrapClaim>(
                await verifiedOwner.IssueAsync(harness.Target, TimeSpan.FromMinutes(5))).IssuerKind);

        var noClaim = new FixedHostedClaimSource(null);
        var hostedWithoutConfiguredKey = new HostedControlPlaneBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, null, noClaim, new Ed25519Verifier());
        Assert.Null(await hostedWithoutConfiguredKey.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));

        using var keyPair = KeyPair.Generate();
        var targetDigest = InstallationAuditIntegrity.Hash(
            "bootstrap-claim-target/v1", harness.Target.InstallationId, harness.Target.TenantId.Value,
            harness.Target.Principal.Value, harness.Target.Party.Value, harness.Target.CeremonyCorrelationId);
        var payload = new HostedControlPlaneBootstrapAuthority(
            harness.InstallationId,
            targetDigest,
            "hosted-control-plane:test",
            Now,
            Now.AddMinutes(5));
        var signed = await new Ed25519Signer(keyPair).SignAsync(payload, Now, Guid.NewGuid());
        var verifiedHosted = new HostedControlPlaneBootstrapClaimIssuer(
            harness.Clock,
            harness.IdentityFactory,
            keyPair.PrincipalId.AsSpan().ToArray(),
            new FixedHostedClaimSource(signed),
            new Ed25519Verifier());

        var claim = Assert.IsType<BootstrapClaim>(
            await verifiedHosted.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));
        Assert.Equal(BootstrapClaimIssuerKind.HostedControlPlane, claim.IssuerKind);
        Assert.Equal("hosted-control-plane:test", claim.IssuerIdentity);
    }

    [Fact]
    public void No_Public_Constructor_Lets_Another_Issuer_Construct_An_Accepted_Claim()
    {
        Assert.Empty(typeof(BootstrapClaim).GetConstructors());
    }

    [Fact]
    public async Task Absent_Expired_Wrong_Source_And_Wrong_Installation_Claims_Are_Rejected()
    {
        await using var harness = await Harness.CreateAsync();
        var clock = harness.Clock;
        var expired = await harness.IssueClaimAsync("founder", TimeSpan.FromMinutes(1));
        var valid = await harness.IssueClaimAsync("founder");
        var wrongTarget = harness.Target with { Principal = new PrincipalUserId("another-principal") };
        var wrongSource = new BootstrapClaim(
            BootstrapClaimIssuerKind.DesktopOsSession,
            "invented-issuer",
            harness.InstallationId,
            valid.TargetDigest,
            Now,
            Now,
            Now.AddMinutes(5),
            valid.IssuedTimestamp,
            "invented-nonce",
            new object());
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(
            BootstrapClaimRedemptionStatus.ClaimRejected,
            (await harness.Service.RedeemAsync(null, harness.Target)).Status);
        Assert.Equal(
            BootstrapClaimRedemptionStatus.ClaimRejected,
            (await harness.Service.RedeemAsync(expired, harness.Target)).Status);
        Assert.Equal(
            BootstrapClaimRedemptionStatus.ClaimRejected,
            (await harness.Service.RedeemAsync(valid, wrongTarget)).Status);
        Assert.Equal(
            BootstrapClaimRedemptionStatus.ClaimRejected,
            (await harness.Service.RedeemAsync(wrongSource, harness.Target)).Status);
        Assert.Equal(4, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
    }

    [Fact]
    public async Task Redemption_Writes_One_Ordinary_Bootstrap_Administrator_Grant_And_Permanent_Audit()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder");

        var result = await harness.Service.RedeemAsync(claim, harness.Target);

        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed, result.Status);
        var grant = Assert.Single(
            await harness.GrantStore.SnapshotAsync(Tenant), item => item.Role == RoleReference.Administrator);
        Assert.Equal(RoleReference.Administrator, grant.Role);
        Assert.Equal(ScopeExpression.Parse("/"), grant.Scope);
        Assert.Equal(GrantSourceKind.Bootstrap, grant.Grant.Source);
        Assert.Equal(GrantReasonCodes.Bootstrap, grant.Grant.Reason.Code);
        Assert.Equal("desktop-os-session:founder", grant.Grant.Approver.Value);
        Assert.Equal("desktop-os-session:founder", grant.GrantedBy.Value);
        Assert.Equal(GranterKind.Installer, grant.GranterKind);

        await using var identity = harness.IdentityFactory.CreateDbContext();
        var marker = Assert.Single(await identity.BootstrapClaimMarkers.AsNoTracking().ToArrayAsync());
        Assert.Equal(harness.InstallationId, marker.InstallationId);
        Assert.Equal(grant.GrantId.ToString(), marker.GrantId);
        Assert.Equal(BootstrapClaimIssuerKind.DesktopOsSession, marker.IssuerKind);
        Assert.Equal("desktop-os-session:founder", marker.IssuerIdentity);
        Assert.Single(await identity.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == InstallationIdentityAuditEventTypes.BootstrapClaimRedeemed)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Replayed_Claim_And_Surface_Are_Permanently_Absent_After_Redemption_And_Revocation()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder");

        var redeemed = await harness.Service.RedeemAsync(claim, harness.Target);
        var grant = Assert.Single(
            await harness.GrantStore.SnapshotAsync(Tenant), item => item.Role == RoleReference.Administrator);
        // A successor is appended first: ticket 211's not_last_administrator() guard (L619) refuses to revoke
        // the last Administrator in force, and this test's subject is that the bootstrap surface never comes
        // back once a grant has EVER existed — not that the install can be left administrator-less.
        await harness.GrantStore.AppendAsync(Tenant, grant with { GrantId = GrantId.New() });
        await harness.GrantStore.RevokeAsync(
            Tenant,
            grant.GrantId,
            new GrantRevocation(
                new ActorId("admin"),
                Now.AddMinutes(1),
                new GrantReason(GrantReasonCodes.Manual, "test-revocation")));

        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed, redeemed.Status);
        Assert.False(await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
        Assert.Equal(
            BootstrapClaimRedemptionStatus.SurfaceUnavailable,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
    }

    [Fact]
    public async Task Any_Preexisting_Grant_Removes_Discovery_And_Invocation()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.GrantStore.AppendAsync(
            Tenant,
            Grant(
                new GrantId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                GrantSourceKind.Manual,
                GranterKind.Person,
                "existing-admin"),
            "existing-first-grant");

        Assert.False(await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
        Assert.Equal(
            BootstrapClaimRedemptionStatus.SurfaceUnavailable,
            (await harness.Service.RedeemAsync(
                await harness.IssueClaimAsync("founder"), harness.Target)).Status);
        Assert.Equal(5, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
    }

    [Fact]
    public async Task Authorization_Seed_System_Grants_Do_Not_Seal_Founder_Redemption()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.True(await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
        var result = await harness.Service.RedeemAsync(
            await harness.IssueClaimAsync("founder"), harness.Target);

        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed, result.Status);
        Assert.Equal(5, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
    }

    [Fact]
    public async Task One_Hundred_Concurrent_Valid_Redemptions_Create_Exactly_One_Grant_And_Audit()
    {
        await using var harness = await Harness.CreateAsync();
        var issuedClaims = new List<BootstrapClaim>();
        for (var index = 0; index < 100; index++)
            issuedClaims.Add(await harness.IssueClaimAsync("founder"));
        await using var blocker = await BeginImmediateAsync(harness.DatabasePath);
        using var start = new Barrier(101);
        using var attempted = new CountdownEvent(100);
        var active = 0;
        var maxActive = 0;
        harness.Service.FenceAttemptScopeForTests = () =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, current);
            attempted.Signal();
            return new CallbackDisposable(() => Interlocked.Decrement(ref active));
        };
        var claimants = issuedClaims.Select(claim => Task.Factory.StartNew(async () =>
        {
            start.SignalAndWait();
            return await harness.Service.RedeemAsync(claim, harness.Target);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        start.SignalAndWait();
        Assert.True(attempted.Wait(TimeSpan.FromSeconds(20)), "all claimants must attempt the held fence");
        await RollbackAsync(blocker);
        var results = await Task.WhenAll(claimants);

        Assert.True(maxActive >= 2, $"expected overlapping fence attempts, observed {maxActive}");
        Assert.Single(results, result => result.Status == BootstrapClaimRedemptionStatus.Redeemed);
        Assert.Equal(99, results.Count(result =>
            result.Status == BootstrapClaimRedemptionStatus.SurfaceUnavailable));
        Assert.Equal(5, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
        await using var identity = harness.IdentityFactory.CreateDbContext();
        Assert.Single(await identity.BootstrapClaimMarkers.AsNoTracking().ToArrayAsync());
        Assert.Single(await identity.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == InstallationIdentityAuditEventTypes.BootstrapClaimRedeemed)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Marker_Is_Retained_After_The_Ordinary_Grant_Is_Deleted()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder");
        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
        await ExecuteAsync(harness.DatabasePath,
            "DELETE FROM search_grants; " +
            "DELETE FROM installation_audit_envelopes " +
            "WHERE event_type = 'InstallationBootstrapClaimRedeemed';");

        Assert.False(await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
        Assert.Equal(BootstrapClaimRedemptionStatus.SurfaceUnavailable,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
        await using var identity = harness.IdentityFactory.CreateDbContext();
        Assert.Single(await identity.BootstrapClaimMarkers.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Marker_Table_Reset_With_Bootstrap_Audit_Retained_Stays_Retired()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder");
        Assert.Equal(BootstrapClaimRedemptionStatus.Redeemed,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
        await ExecuteAsync(harness.DatabasePath,
            "DELETE FROM bootstrap_claim_marker; DELETE FROM search_grants;");

        Assert.False(await harness.Service.IsSurfaceAvailableAsync(harness.InstallationId, Tenant));
        Assert.Equal(BootstrapClaimRedemptionStatus.SurfaceUnavailable,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
    }

    [Fact]
    public async Task Claim_That_Expires_While_Waiting_On_A_Real_Write_Lock_Is_Rejected()
    {
        await using var harness = await Harness.CreateAsync();
        var claim = await harness.IssueClaimAsync("founder", TimeSpan.FromMinutes(1));
        await using var blocker = await BeginImmediateAsync(harness.DatabasePath);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.FenceAttemptScopeForTests = () =>
        {
            attempted.TrySetResult();
            return new CallbackDisposable(() => { });
        };

        var redemption = Task.Run(() => harness.Service.RedeemAsync(claim, harness.Target));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await RollbackAsync(blocker);

        Assert.Equal(BootstrapClaimRedemptionStatus.ClaimRejected, (await redemption).Status);
        Assert.Equal(4, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
    }

    [Fact]
    public async Task Valid_Acceptance_Token_With_Unknown_Issuer_Enum_Is_Rejected()
    {
        await using var harness = await Harness.CreateAsync();
        var valid = await harness.IssueClaimAsync("founder");
        var unknown = new BootstrapClaim(
            (BootstrapClaimIssuerKind)int.MaxValue,
            valid.IssuerIdentity,
            valid.InstallationId,
            valid.TargetDigest,
            valid.IssuedAt,
            valid.NotBefore,
            valid.ExpiresAt,
            valid.IssuedTimestamp,
            valid.Nonce,
            valid.Acceptance);

        Assert.Equal(BootstrapClaimRedemptionStatus.ClaimRejected,
            (await harness.Service.RedeemAsync(unknown, harness.Target)).Status);
    }

    [Fact]
    public async Task Replay_Never_Extends_The_Persisted_Installation_Deadline_And_Backward_Time_Is_Rejected()
    {
        await using var harness = await Harness.CreateAsync();
        var first = await harness.IssueClaimAsync("founder");
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var replay = await harness.IssueClaimAsync("founder");
        Assert.Equal(first.ExpiresAt, replay.ExpiresAt);

        harness.Clock.RewindWallClock(TimeSpan.FromMinutes(3));
        var issuer = new DesktopOsSessionBootstrapClaimIssuer(
            harness.Clock, harness.IdentityFactory, "founder", new FixedDesktopEvidence("founder", true));
        Assert.Null(await issuer.IssueAsync(harness.Target, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Migration_Upgrade_Backfills_Retirement_And_Grant_Wipe_Cannot_Reopen_Bootstrap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bootstrap-upgrade-{Guid.NewGuid():N}.db");
        var factory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(path);
        try
        {
            await using (var oldSchema = factory.CreateDbContext())
            {
                await oldSchema.Database.MigrateAsync("20260728043828_LegacyBearerCutoverEvidence");
            }
            var clock = new MutableClock(Now);
            var bootstrap = new InstallationFounderBootstrapService(factory, clock);
            var created = await bootstrap.InitializeAsync(InstallationFounderBootstrapServiceTests.Command(
                "founder", InstallationFounderBootstrapCeremony.CorrelationId));
            await CreateGrantTablesAsync(path);
            var grantStore = new NodeEfGrantStore(new SharedSearchFactory(path));
            await grantStore.AppendAsync(
                Tenant,
                Grant(new GrantId(Guid.NewGuid()), GrantSourceKind.Manual, GranterKind.Person, "legacy-admin"),
                "legacy-grant");

            await using (var upgraded = factory.CreateDbContext())
            {
                await upgraded.Database.MigrateAsync();
                var marker = Assert.Single(await upgraded.BootstrapClaimMarkers.AsNoTracking().ToArrayAsync());
                Assert.Equal(created.InstallationIdentityId, marker.InstallationId);
                await upgraded.Database.ExecuteSqlRawAsync(
                    "DELETE FROM search_grants; DELETE FROM installation_access_grants;");
            }

            var service = new BootstrapClaimRedemptionService(
                factory, grantStore,
                new InitialGrantIssuanceService(grantStore, TestAuthorization.AllowGate(), clock),
                AuthorizationSeedProfile.Production,
                clock);
            Assert.False(await service.IsSurfaceAvailableAsync(created.InstallationIdentityId!, Tenant));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Audit_Refusal_Rolls_Back_Grant_Marker_And_Claim_Consumption()
    {
        await using var harness = await Harness.CreateAsync();
        await using (var identity = harness.IdentityFactory.CreateDbContext())
        {
            await identity.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER refuse_bootstrap_claim_audit
                BEFORE INSERT ON installation_audit_envelopes
                WHEN NEW.event_type = 'InstallationBootstrapClaimRedeemed'
                BEGIN
                    SELECT RAISE(ABORT, 'bootstrap audit refused');
                END;
                """);
        }

        var claim = await harness.IssueClaimAsync("founder");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Service.RedeemAsync(claim, harness.Target));

        Assert.Equal(4, (await harness.GrantStore.SnapshotAsync(Tenant)).Count);
        await using var verify = harness.IdentityFactory.CreateDbContext();
        Assert.Empty(await verify.BootstrapClaimMarkers.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == InstallationIdentityAuditEventTypes.BootstrapClaimRedeemed)
            .ToArrayAsync());
        await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_bootstrap_claim_audit;");
        Assert.Equal(
            BootstrapClaimRedemptionStatus.Redeemed,
            (await harness.Service.RedeemAsync(claim, harness.Target)).Status);
    }

    private static AccessGrant Grant(
        GrantId id,
        GrantSourceKind source,
        GranterKind granterKind,
        string actor) =>
        new(
            id,
            Tenant,
            new ActorId(Founder.Value),
            RoleReference.Administrator,
            ScopeExpression.Parse("/"),
            GrantResidency.Cache,
            new GrantValidity(Now),
            granterKind,
            new ActorId(actor),
            Now,
            new GrantProvenance(
                source,
                new GrantReason(source == GrantSourceKind.Bootstrap
                    ? GrantReasonCodes.Bootstrap
                    : GrantReasonCodes.Manual),
                new ActorId(actor)),
            Now);

    private static AccessGrant SystemSeedGrant(
        GrantId id,
        string principal,
        RoleReference role,
        string sourceReference)
    {
        var installer = new ActorId("installer:authorization-definition-seed");
        return new AccessGrant(
            id,
            Tenant,
            new ActorId(principal),
            role,
            ScopeExpression.Parse("/"),
            GrantResidency.Cache,
            new GrantValidity(Now),
            GranterKind.Installer,
            installer,
            Now,
            new GrantProvenance(
                GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap, sourceReference),
                installer),
            Now);
    }

    private static async Task CopyRealAuthorizationSeedAsync(
        Harness harness,
        bool includeDevelopmentGrants = true,
        string? omittedSource = null,
        bool replaceIds = false)
    {
        await using var provider = new ServiceCollection()
            .AddAccessGrantModule()
            .BuildServiceProvider();
        var source = provider.GetRequiredService<IGrantStore>();
        await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
            .InstallAsync(
                Tenant,
                Now,
                includeDevelopmentGrants
                    ? AuthorizationSeedProfile.Development
                    : AuthorizationSeedProfile.Production);
        var sourceReferences = includeDevelopmentGrants
            ? new[]
            {
                AccessGrantAuthorizationSeed.SchedulerGrantSource,
                AccessGrantAuthorizationSeed.DevIndexerGrantSource,
                AccessGrantAuthorizationSeed.DevWorkflowSeederGrantSource,
                AccessGrantAuthorizationSeed.NodeOperatorGrantSource,
            }
            : [
                AccessGrantAuthorizationSeed.SchedulerGrantSource,
                AccessGrantAuthorizationSeed.NodeOperatorGrantSource,
            ];
        for (var index = 0; index < sourceReferences.Length; index++)
        {
            var sourceReference = sourceReferences[index];
            if (string.Equals(sourceReference, omittedSource, StringComparison.Ordinal)) continue;
            var grant = Assert.IsType<AccessGrant>(
                await source.FindBySourceReferenceAsync(Tenant, sourceReference));
            if (replaceIds)
            {
                grant = grant with
                {
                    GrantId = new GrantId(Guid.Parse($"bbbbbbbb-0000-0000-0000-{index + 1:D12}")),
                };
            }
            await harness.GrantStore.AppendAsync(Tenant, grant, sourceReference);
        }
    }

    private sealed class CompositionProbeCompleteException : Exception;

    private static async Task<SqliteConnection> BeginImmediateAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Default Timeout=30");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                return;
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly InstallationFounderBootstrapServiceTests.TestIdentityDatabase _identity;

        private Harness(
            InstallationFounderBootstrapServiceTests.TestIdentityDatabase identity,
            string installationId,
            MutableClock clock)
        {
            _identity = identity;
            InstallationId = installationId;
            Clock = clock;
            GrantStore = new NodeEfGrantStore(new SharedSearchFactory(identity.Path));
            IdentityFactory = identity.Factory;
            Service = new BootstrapClaimRedemptionService(
                identity.Factory,
                GrantStore,
                new InitialGrantIssuanceService(GrantStore, TestAuthorization.AllowGate(), clock),
                AuthorizationSeedProfile.Development,
                clock);
            Target = new BootstrapGrantTarget(
                Tenant,
                Founder,
                FounderParty,
                InstallationFounderBootstrapCeremony.CorrelationId,
                installationId);
        }

        public string InstallationId { get; }
        public string DatabasePath => _identity.Path;
        public MutableClock Clock { get; }
        public NodeEfGrantStore GrantStore { get; }
        public IDbContextFactory<NodeLocalInstallationIdentityDbContext> IdentityFactory { get; }
        public BootstrapClaimRedemptionService Service { get; }
        public BootstrapGrantTarget Target { get; }

        public async Task<BootstrapClaim> IssueClaimAsync(
            string processUser,
            TimeSpan? lifetime = null) =>
            Assert.IsType<BootstrapClaim>(await new DesktopOsSessionBootstrapClaimIssuer(
                    Clock,
                    IdentityFactory,
                    processUser,
                    new FixedDesktopEvidence(processUser, true))
                .IssueAsync(Target, lifetime ?? TimeSpan.FromMinutes(5)));

        public static async Task<Harness> CreateAsync(bool seedAuthorization = true)
        {
            var identity = await InstallationFounderBootstrapServiceTests.TestIdentityDatabase.CreateAsync();
            var clock = new MutableClock(Now);
            var bootstrap = new InstallationFounderBootstrapService(identity.Factory, clock);
            var created = await bootstrap.InitializeAsync(InstallationFounderBootstrapServiceTests.Command(
                "founder",
                InstallationFounderBootstrapCeremony.CorrelationId));
            await CreateGrantTablesAsync(identity.Path);
            var harness = new Harness(identity, created.InstallationIdentityId!, clock);
            if (seedAuthorization) await CopyRealAuthorizationSeedAsync(harness);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await _identity.DisposeAsync();
        }
    }

    internal static async Task CreateGrantTablesAsync(string databasePath)
    {
        await using var context = new SharedSearchFactory(databasePath).CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE search_grants (
                grant_id TEXT NOT NULL, tenant_id TEXT NOT NULL, subject_id TEXT NOT NULL,
                role_vocabulary TEXT NOT NULL, role_name TEXT NOT NULL, scope_type INTEGER NOT NULL,
                scope_value TEXT NOT NULL, residency INTEGER NOT NULL, validity_from_unix_ms INTEGER NOT NULL,
                validity_until_unix_ms INTEGER NULL, status INTEGER NOT NULL, granter_kind INTEGER NOT NULL,
                granted_by TEXT NOT NULL, granted_at_unix_ms INTEGER NOT NULL, source INTEGER NOT NULL,
                reason_code TEXT NOT NULL, reason_reference TEXT NULL, approver TEXT NOT NULL,
                last_reviewed_at_unix_ms INTEGER NOT NULL, last_reviewed_by TEXT NULL,
                validity_changed_by TEXT NULL, validity_change_reason_code TEXT NULL,
                validity_change_reason_reference TEXT NULL, revoked_by TEXT NULL,
                revoked_at_unix_ms INTEGER NULL, revocation_reason_code TEXT NULL,
                revocation_reason_reference TEXT NULL, source_reference TEXT NULL, owner_version INTEGER NOT NULL,
                PRIMARY KEY (tenant_id, grant_id));
            CREATE UNIQUE INDEX ux_test_grant_source ON search_grants(tenant_id, source_reference);
            CREATE TABLE search_grant_authorization_epochs (
                tenant_id TEXT NOT NULL, principal_id TEXT NOT NULL, authorization_epoch INTEGER NOT NULL,
                PRIMARY KEY (tenant_id, principal_id));
            CREATE TABLE authorization_tenant_versions (
                tenant_id TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL);
            """);
    }

    internal sealed class SharedSearchFactory(string databasePath)
        : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False")
                .Options;
            return new NodeLocalSearchDbContext(options);
        }
    }

    internal sealed class FixedDesktopEvidence(string userName, bool isInteractive)
        : IDesktopOsSessionEvidence
    {
        public bool IsInteractive => isInteractive;
        public string UserName => userName;
    }

    private sealed class FixedOwnerEvidence(bool owned) : IFileSystemOwnerEvidence
    {
        public bool IsOwnedByCurrentProcessUser(string dataDirectory) => owned;
    }

    private sealed class FixedHostedClaimSource(
        SignedOperation<HostedControlPlaneBootstrapAuthority>? claim)
        : IHostedControlPlaneBootstrapClaimSource
    {
        public Task<SignedOperation<HostedControlPlaneBootstrapAuthority>?> ObtainAsync(
            BootstrapGrantTarget target,
            CancellationToken cancellationToken) => Task.FromResult(claim);
    }

    public sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by)
        {
            now += by;
            _timestamp += by.Ticks;
        }

        public void RewindWallClock(TimeSpan by) => now -= by;
    }
}
