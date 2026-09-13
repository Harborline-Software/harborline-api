using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3342 — the founder-bootstrap ceremony: the ONE production path that gives an installation
/// its first v2 account, root binding, and root installation grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline gate is proven at the route, not at the store.</b>
/// <see cref="Ceremony_Turns_A_Challenge_Refusing_Installation_Into_One_That_Issues_A_Challenge"/>
/// drives the REAL <c>POST /api/session/account-challenge</c> handler over the REAL
/// <see cref="WebAccountAccessChallengeIssuer"/>, the REAL <see cref="WebAntiforgeryPolicy"/>, and
/// the REAL ADR 0097 Argon2id hasher against real SQLCipher-shaped SQLite stores — 401 before the
/// ceremony, a challenge cookie after it. Nothing in that path is stubbed.
/// </para>
/// <para>
/// <b>The credential is minted the way production mints it.</b> The node's
/// <c>hash-web-password</c> subcommand hashes for <c>IPasswordHasher&lt;NodeWebUser&gt;</c> while
/// the challenge issuer verifies through <c>IPasswordHasher&lt;InstallationAccountRecord&gt;</c>.
/// That cross-type round trip is load-bearing for this ceremony, so the test mints exactly as the
/// subcommand does rather than with the verifying hasher.
/// </para>
/// <para>
/// <b>Mutation-proof teeth.</b> (1) Replace either derived ceremony coordinate with a generated one
/// and <see cref="Restart_Under_Unchanged_Authority_Replays_Idempotently"/> or
/// <see cref="Restart_Under_A_Changed_Environment_Credential_Is_Refused"/> fails — the first because
/// an unchanged restart would report a bare "already initialized" instead of an authenticated
/// idempotent replay, the second because a silently swapped environment credential would become
/// indistinguishable from a normal restart. (2) Add any check-then-write of your own and
/// <see cref="Concurrent_First_Boot_Ceremonies_Mint_Exactly_One_Root"/> fails. (3) Turn any
/// precondition into a default and <see cref="Incomplete_Bootstrap_Authority_Writes_Nothing"/> fails
/// on a non-empty store.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3342")]
public sealed class InstallationFounderBootstrapCeremonyTests
{
    private const string FounderUsername = "founder";
    private const string FounderPassword = "correct horse battery staple installation";

    // A canonical 95-char ADR 0066 fingerprint standing in for the install's root Ed25519 key.
    private static readonly string RootFingerprint = string.Join(":", Enumerable.Repeat("AB", 32));
    private static readonly string OtherRootFingerprint = string.Join(":", Enumerable.Repeat("CD", 32));

    [Fact]
    public async Task Ceremony_Turns_A_Challenge_Refusing_Installation_Into_One_That_Issues_A_Challenge()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var verifyingHasher = CreateHasher<InstallationAccountRecord>();
        var issuer = new WebAccountAccessChallengeIssuer(
            stores.IdentityFactory,
            stores.SessionFactory,
            verifyingHasher,
            FixtureV1AuthorityGate.Admitting,
            TimeProvider.System);
        var antiforgery = new WebAntiforgeryPolicy(
            stores.SessionFactory,
            new WebSelectedSessionStore(stores.SessionFactory),
            new WebAntiforgeryStateStore(stores.SessionFactory, TimeProvider.System),
            TimeProvider.System);

        // BEFORE: identity.Accounts is empty, so the challenge route refuses every actor.
        Assert.Empty(await stores.AccountsAsync());
        var before = await PostAccountChallengeAsync(antiforgery, issuer, FounderUsername, FounderPassword);
        Assert.Equal(StatusCodes.Status401Unauthorized, before.StatusCode);
        Assert.Contains("authentication_failed", before.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(WebSessionCookieNames.Challenge, before.SetCookie, StringComparison.Ordinal);

        var outcome = await CreateCeremony(stores, MintFounderCredential(FounderPassword)).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, outcome.Status);
        Assert.NotNull(outcome.AccountId);

        // AFTER: the same request now authenticates and receives a challenge audience.
        var after = await PostAccountChallengeAsync(antiforgery, issuer, FounderUsername, FounderPassword);
        Assert.Equal(StatusCodes.Status200OK, after.StatusCode);
        Assert.Contains($"{WebSessionCookieNames.Challenge}=", after.SetCookie, StringComparison.Ordinal);
        Assert.Equal("no-store", after.CacheControl);
        // The refusal path is unchanged for a wrong credential — the ceremony minted ONE account,
        // it did not make the front door permissive.
        var wrongPassword = await PostAccountChallengeAsync(
            antiforgery, issuer, FounderUsername, FounderPassword + "!");
        Assert.Equal(StatusCodes.Status401Unauthorized, wrongPassword.StatusCode);
    }

    [Fact]
    public async Task Established_Installation_Carries_One_Account_Root_Epoch_Grant_And_Audit()
    {
        await using var stores = await CeremonyStores.CreateAsync();

        var outcome = await CreateCeremony(stores, MintFounderCredential(FounderPassword)).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, outcome.Status);
        await using var context = stores.IdentityFactory.CreateDbContext();
        var account = Assert.Single(await context.Accounts.AsNoTracking().ToArrayAsync());
        Assert.Equal("FOUNDER", account.NormalizedUsername);
        Assert.Equal(InstallationAccountStatus.Active, account.Status);
        Assert.Equal(Argon2idCredentialArtifact.AlgorithmId, account.CredentialAlgorithm);

        var epoch = Assert.Single(await context.RootKeyEpochs.AsNoTracking().ToArrayAsync());
        Assert.Equal(RootFingerprint, epoch.RootPublicKeyFingerprint);
        Assert.Equal(1, epoch.EpochNumber);

        var grant = Assert.Single(await context.InstallationAccessGrants.AsNoTracking().ToArrayAsync());
        Assert.Equal(account.AccountId, grant.AccountId);
        Assert.Equal(InstallationAccessGrantStatus.Active, grant.Status);
        Assert.Contains("installation:ownership:transfer", grant.PermissionsJson, StringComparison.Ordinal);

        var envelope = Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Equal(InstallationFounderBootstrapCeremony.CorrelationId, envelope.CorrelationId);
        Assert.Equal("local-installation-console", envelope.ActorId);
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
        // The audit envelope carries no credential material of any kind.
        Assert.DoesNotContain("argon2", envelope.PayloadDigest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argon2", envelope.CommandFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    // Ticket 360. Every other ceremony test here constructs FixedDesktopEvidence(..., true), so the
    // whole suite asserted the INTERACTIVE path and the headless one had no coverage -- because it
    // had no implementation. DesktopOsSessionBootstrapClaimIssuer declines when
    // Environment.UserInteractive is false, which is every Windows service and every launchd daemon,
    // so a headless node could not issue a founder bootstrap claim at all. That is the MVP's own
    // deployment shape, and it was read for weeks as a flaky macOS test.
    //
    // This asserts the CLAIM, not RunAsync: RunAsync establishes the credential and succeeds
    // headless either way, which is why a first version of this test passed with the fallback
    // deleted. The claim is what FounderTenantMembershipAttachService needs, and its absence is
    // what returns Unavailable.
    [Fact]
    public async Task Headless_Host_Issues_A_Filesystem_Owner_Claim()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var clock = new BootstrapClaimRedemptionTests.MutableClock(
            new DateTimeOffset(2026, 9, 13, 1, 0, 0, TimeSpan.Zero));
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"t360-headless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ceremony = new InstallationFounderBootstrapCeremony(
                new InstallationFounderBootstrapService(stores.IdentityFactory, clock),
                Options.Create(new NodeWebClientOptions
                {
                    Enabled = true,
                    FounderUsername = FounderUsername,
                    FounderPasswordHash = MintFounderCredential(FounderPassword),
                }),
                RootFingerprint,
                stores.IdentityFactory,
                clock,
                // No desktop session, and a process user that is NOT the founder -- exactly a service
                // account. BOTH clauses of the desktop issuer's guard fail, so the filesystem-owner
                // issuer carries this or nothing does.
                new BootstrapClaimRedemptionTests.FixedDesktopEvidence("service-account", false),
                dataDirectory);

            Assert.Equal(
                InstallationFounderBootstrapCeremonyStatus.Established,
                (await ceremony.RunAsync()).Status);

            await using var context = stores.IdentityFactory.CreateDbContext();
            var installationId = (await context.InstallationIdentities.AsNoTracking().SingleAsync())
                .InstallationIdentityId;
            var tenant = TenantId.FromString("founder-ceremony-tenant");
            var principal = FounderTenantMembershipAttachService.DerivePrincipal(
                tenant, InstallationFounderBootstrapCeremony.CorrelationId);
            var claim = Assert.IsType<BootstrapClaim>(await ceremony.IssueClaimForTargetAsync(
                new BootstrapGrantTarget(
                    tenant,
                    principal,
                    new CanonicalPartyReference("founder-party"),
                    InstallationFounderBootstrapCeremony.CorrelationId,
                    installationId),
                CancellationToken.None));
            Assert.Equal(BootstrapClaimIssuerKind.SelfHostedFileSystemOwner, claim.IssuerKind);
            Assert.Equal(installationId, claim.InstallationId);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Environment_Ceremony_Issues_A_Desktop_Os_Session_Claim_Instead_Of_Grant_Bypass()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var clock = new BootstrapClaimRedemptionTests.MutableClock(
            new DateTimeOffset(2026, 9, 2, 14, 0, 0, TimeSpan.Zero));
        var ceremony = new InstallationFounderBootstrapCeremony(
            new InstallationFounderBootstrapService(stores.IdentityFactory, clock),
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = FounderUsername,
                FounderPasswordHash = MintFounderCredential(FounderPassword),
            }),
            RootFingerprint,
            stores.IdentityFactory,
            clock,
            new BootstrapClaimRedemptionTests.FixedDesktopEvidence(FounderUsername, true),
            Path.Combine(Path.GetTempPath(), $"t360-interactive-{Guid.NewGuid():N}"));

        Assert.Equal(
            InstallationFounderBootstrapCeremonyStatus.Established,
            (await ceremony.RunAsync()).Status);

        await using var context = stores.IdentityFactory.CreateDbContext();
        var installationId = (await context.InstallationIdentities.AsNoTracking().SingleAsync())
            .InstallationIdentityId;
        var tenant = TenantId.FromString("founder-ceremony-tenant");
        var principal = FounderTenantMembershipAttachService.DerivePrincipal(
            tenant, InstallationFounderBootstrapCeremony.CorrelationId);
        var claim = Assert.IsType<BootstrapClaim>(await ceremony.IssueClaimForTargetAsync(
            new BootstrapGrantTarget(
                tenant,
                principal,
                new CanonicalPartyReference("founder-party"),
                InstallationFounderBootstrapCeremony.CorrelationId,
                installationId),
            CancellationToken.None));
        Assert.Equal(BootstrapClaimIssuerKind.DesktopOsSession, claim.IssuerKind);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), claim.ExpiresAt);
        Assert.Equal(installationId, claim.InstallationId);
    }

    [Fact]
    public async Task Restart_Under_Unchanged_Authority_Replays_Idempotently()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);

        var first = await CreateCeremony(stores, credential).RunAsync();
        // A fresh ceremony instance per run: this is a host RESTART, not a reused object.
        var second = await CreateCeremony(stores, credential).RunAsync();
        var third = await CreateCeremony(stores, credential).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, first.Status);
        foreach (var replay in new[] { second, third })
        {
            Assert.Equal(InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished, replay.Status);
            // The authenticated-replay reason, NOT the bare already-initialized one: a generated
            // correlation id could never produce this.
            Assert.Equal("installation-bootstrap.idempotent_replay", replay.Reason);
            Assert.Equal(first.AccountId, replay.AccountId);
        }

        await AssertExactlyOneRootAsync(stores);
    }

    [Fact]
    public async Task Restart_Under_A_Changed_Environment_Credential_Is_Refused()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var established = await CreateCeremony(stores, MintFounderCredential(FounderPassword)).RunAsync();
        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, established.Status);
        var durable = await SingleAccountAsync(stores);

        var rotatedCredential = await CreateCeremony(stores, MintFounderCredential("a different password"))
            .RunAsync();
        var renamedFounder = await CreateCeremony(
                stores,
                MintFounderCredential(FounderPassword),
                username: "someone-else")
            .RunAsync();

        foreach (var refusal in new[] { rotatedCredential, renamedFounder })
        {
            Assert.Equal(
                InstallationFounderBootstrapCeremonyStatus.RefusedChangedAuthority,
                refusal.Status);
            Assert.Equal("installation-bootstrap.changed_authority_refused", refusal.Reason);
            Assert.Null(refusal.AccountId);
        }

        // The durable founder is untouched: same id, same username, same credential, same versions.
        var after = await SingleAccountAsync(stores);
        Assert.Equal(durable.AccountId, after.AccountId);
        Assert.Equal(durable.NormalizedUsername, after.NormalizedUsername);
        Assert.Equal(durable.CredentialHash, after.CredentialHash);
        Assert.Equal(durable.CredentialVersion, after.CredentialVersion);
        Assert.Equal(durable.SecurityVersion, after.SecurityVersion);
        Assert.Equal(durable.OwnerVersion, after.OwnerVersion);
        await AssertExactlyOneRootAsync(stores);
    }

    /// <summary>
    /// The regression that the composed-host gate could not have caught, because it needs a SECOND
    /// account to exist first. Before this test's fix the replay path resolved the founder by taking
    /// the only row in <c>Accounts</c> and the only row in <c>InstallationAccessGrants</c>. Harmless
    /// while the authority was dormant — but this card runs the ceremony inside
    /// <see cref="IHostedService.StartAsync"/> on EVERY boot, and <c>WebJoinerAccountMinter</c>
    /// (reached from <c>POST /api/session/account-setup-accept</c>) adds a second account the moment
    /// an invited joiner accepts. <c>SingleAsync</c> throws on more than one row, the throw propagates
    /// out of <c>Host.StartAsync</c>, and the node stops starting permanently, with no recovery short
    /// of editing code or performing surgery on the encrypted store.
    /// </summary>
    [Fact]
    public async Task Restart_After_A_Joiner_Account_Exists_Still_Replays_As_The_Founder()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);

        var established = await CreateCeremony(stores, credential).RunAsync();
        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, established.Status);

        await AddJoinerAccountAsync(stores, "joiner");

        var restart = await CreateCeremony(stores, credential).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished, restart.Status);
        Assert.Equal("installation-bootstrap.idempotent_replay", restart.Reason);
        // The FOUNDER's account, not whichever row happened to come back first.
        Assert.Equal(established.AccountId, restart.AccountId);

        // And the host's startup runner completes instead of aborting the boot.
        await RunHostedCeremonyAsync(stores, credential);

        await using var context = stores.IdentityFactory.CreateDbContext();
        Assert.Equal(2, await context.Accounts.CountAsync());
        Assert.Equal(2, await context.InstallationAccessGrants.CountAsync());
        // The replay is still read-only: one installation, one audit envelope, one head.
        Assert.Equal(1, await context.InstallationIdentities.CountAsync());
        Assert.Equal(1, await context.AuditEnvelopes.CountAsync());
        Assert.Equal(1, await context.AuditHeads.CountAsync());
    }

    /// <summary>
    /// The zero-row half of the same defect, CLASSIFIED rather than left to throw out of a query.
    /// Founder evidence that authenticates but whose root installation grant is absent is a
    /// fail-closed divergence — ADR 0160 R3-F makes that grant the installation's root authority — so
    /// the host must not start through it (R3-H). Before this fix the same condition surfaced as an
    /// unclassified "Sequence contains no elements".
    /// </summary>
    [Fact]
    public async Task Authenticated_Evidence_Without_Its_Root_Grant_Refuses_And_Stops_The_Host()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);
        Assert.Equal(
            InstallationFounderBootstrapCeremonyStatus.Established,
            (await CreateCeremony(stores, credential).RunAsync()).Status);

        // Out-of-band surgery on the encrypted store: the root grant is gone while the audit chain
        // still attests that the ceremony minted it. The chain itself authenticates, so this is NOT
        // the existing audit_evidence_invalid condition, and the presented command fingerprint still
        // matches, so it is NOT a changed-authority replay either.
        await using (var surgery = stores.IdentityFactory.CreateDbContext())
        {
            surgery.InstallationAccessGrants.RemoveRange(
                await surgery.InstallationAccessGrants.ToArrayAsync());
            await surgery.SaveChangesAsync();
        }

        var outcome = await CreateCeremony(stores, credential).RunAsync();

        Assert.Equal(
            InstallationFounderBootstrapCeremonyStatus.RefusedInvalidFounderEvidence,
            outcome.Status);
        Assert.Equal("installation-bootstrap.founder_grant_missing", outcome.Reason);
        Assert.Null(outcome.AccountId);

        var abort = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunHostedCeremonyAsync(stores, credential));
        Assert.Contains("audit_evidence_invalid", abort.Message, StringComparison.Ordinal);

        // The refusal wrote nothing — it did not "repair" the installation by re-minting a root.
        await using var context = stores.IdentityFactory.CreateDbContext();
        Assert.Equal(0, await context.InstallationAccessGrants.CountAsync());
        Assert.Equal(1, await context.InstallationIdentities.CountAsync());
        Assert.Equal(1, await context.Accounts.CountAsync());
        Assert.Equal(1, await context.AuditEnvelopes.CountAsync());
    }

    /// <summary>
    /// The companion to the regression above, and the reason the founder is resolved by the ceremony's
    /// own correlation rather than by the founder's username: a governed rename or credential rotation
    /// must not re-classify an unchanged replay. <c>InstallationFounderBootstrapServiceTests</c>
    /// asserts this at the authority; this asserts the ceremony inherits it.
    /// </summary>
    [Fact]
    public async Task Replay_Survives_Later_Governed_Mutation_Of_The_Founder_Account()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);
        var established = await CreateCeremony(stores, credential).RunAsync();
        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.Established, established.Status);

        await using (var governed = stores.IdentityFactory.CreateDbContext())
        {
            var founder = await governed.Accounts.SingleAsync();
            founder.NormalizedUsername = "RENAMED";
            founder.CredentialHash = MintFounderCredential("a rotated password");
            founder.CredentialVersion++;
            founder.SecurityVersion++;
            await governed.SaveChangesAsync();
        }

        var restart = await CreateCeremony(stores, credential).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished, restart.Status);
        Assert.Equal("installation-bootstrap.idempotent_replay", restart.Reason);
        Assert.Equal(established.AccountId, restart.AccountId);
    }

    /// <summary>
    /// The command is a positional record on a live startup path, so its compiler-generated
    /// <c>ToString()</c> would otherwise put the founder's Argon2id artifact into any structured log
    /// call that formatted it.
    /// </summary>
    [Fact]
    public void Bootstrap_Command_Does_Not_Print_Its_Secret_Members()
    {
        const string distinctiveUsername = "founder-name-marker-9f3a";
        var credential = MintFounderCredential(FounderPassword);
        var command = new InstallationFounderBootstrapCommand(
            distinctiveUsername,
            credential,
            InstallationFounderBootstrapCeremony.DeriveCredentialCeremonyId(RootFingerprint, credential),
            RootFingerprint,
            InstallationFounderBootstrapCeremony.CorrelationId);

        var printed = command.ToString();

        Assert.DoesNotContain(credential, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(distinctiveUsername, printed, StringComparison.OrdinalIgnoreCase);
        // Still diagnostically useful: the non-secret coordinates survive.
        Assert.Contains(RootFingerprint, printed, StringComparison.Ordinal);
        Assert.Contains(
            InstallationFounderBootstrapCeremony.CorrelationId, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_First_Boot_Ceremonies_Mint_Exactly_One_Root()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);

        // Twenty-four racing host starts against one data directory, each a fresh ceremony over the
        // same local bootstrap authority — the shape two processes on one install would produce.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            CreateCeremony(stores, credential).RunAsync()));

        Assert.Single(
            outcomes,
            outcome => outcome.Status == InstallationFounderBootstrapCeremonyStatus.Established);
        Assert.All(
            outcomes.Where(o => o.Status != InstallationFounderBootstrapCeremonyStatus.Established),
            outcome => Assert.Equal(
                InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished,
                outcome.Status));
        await AssertExactlyOneRootAsync(stores);
    }

    [Theory]
    [InlineData(false, FounderUsername, true, "installation-bootstrap.web_profile_disabled")]
    [InlineData(true, null, true, "installation-bootstrap.founder_credential_not_provisioned")]
    [InlineData(true, "   ", true, "installation-bootstrap.founder_credential_not_provisioned")]
    [InlineData(true, FounderUsername, false, "installation-bootstrap.founder_credential_not_provisioned")]
    public async Task Incomplete_Bootstrap_Authority_Writes_Nothing(
        bool enabled,
        string? username,
        bool provisionCredential,
        string expectedReason)
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var options = Options.Create(new NodeWebClientOptions
        {
            Enabled = enabled,
            FounderUsername = username,
            FounderPasswordHash = provisionCredential ? MintFounderCredential(FounderPassword) : null,
        });

        var outcome = await new InstallationFounderBootstrapCeremony(
            new InstallationFounderBootstrapService(stores.IdentityFactory, TimeProvider.System),
            options,
            RootFingerprint,
            TimeProvider.System,
            Path.Combine(Path.GetTempPath(), $"t360-ceremony-{Guid.NewGuid():N}")).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority, outcome.Status);
        Assert.Equal(expectedReason, outcome.Reason);
        await AssertNothingWrittenAsync(stores);
    }

    [Fact]
    public async Task Non_Conformant_Credential_Artifact_Writes_Nothing()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var salt = Convert.ToBase64String(new byte[16]);
        var hash = Convert.ToBase64String(new byte[32]);
        var artifacts = new[]
        {
            // Not a PHC artifact at all.
            "not-argon2id",
            // Canonical PHC shape, but below the ADR 0097 memory floor — a downgraded work factor.
            $"$argon2id$v=19$m=8,t=2,p=1${salt}${hash}",
            // Canonical shape at a conformant work factor, but parallelism above the reviewed ceiling.
            $"$argon2id$v=19$m=19456,t=2,p=4${salt}${hash}",
        };

        foreach (var artifact in artifacts)
        {
            var outcome = await CreateCeremony(stores, artifact).RunAsync();

            Assert.Equal(InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority, outcome.Status);
            Assert.Equal("installation-bootstrap.founder_credential_artifact_rejected", outcome.Reason);
        }

        await AssertNothingWrittenAsync(stores);
    }

    [Fact]
    public async Task Unusable_Root_Binding_Or_Over_Long_Username_Writes_Nothing()
    {
        await using var stores = await CeremonyStores.CreateAsync();
        var credential = MintFounderCredential(FounderPassword);

        var noRoot = await new InstallationFounderBootstrapCeremony(
            new InstallationFounderBootstrapService(stores.IdentityFactory, TimeProvider.System),
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = FounderUsername,
                FounderPasswordHash = credential,
            }),
            "not-a-fingerprint",
            TimeProvider.System,
            Path.Combine(Path.GetTempPath(), $"t360-noroot-{Guid.NewGuid():N}")).RunAsync();

        var longUsername = await CreateCeremony(
            stores,
            credential,
            username: new string('u', 321)).RunAsync();

        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority, noRoot.Status);
        Assert.Equal("installation-bootstrap.root_binding_unavailable", noRoot.Reason);
        Assert.Equal(InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority, longUsername.Status);
        Assert.Equal("installation-bootstrap.founder_username_out_of_range", longUsername.Reason);
        await AssertNothingWrittenAsync(stores);
    }

    [Fact]
    public void Ceremony_Coordinates_Are_Derived_From_The_Authority_Not_Generated()
    {
        var credential = MintFounderCredential(FounderPassword);
        var other = MintFounderCredential(FounderPassword);

        var ceremonyId = InstallationFounderBootstrapCeremony
            .DeriveCredentialCeremonyId(RootFingerprint, credential);

        // Stable for the same inputs — this is what makes an unchanged restart an idempotent replay.
        Assert.Equal(
            ceremonyId,
            InstallationFounderBootstrapCeremony.DeriveCredentialCeremonyId(RootFingerprint, credential));
        // Distinct when the credential artifact changes (a re-hash of the SAME password re-salts, so
        // this also catches a credential re-mint), and when the installation root binding changes.
        Assert.NotEqual(
            ceremonyId,
            InstallationFounderBootstrapCeremony.DeriveCredentialCeremonyId(RootFingerprint, other));
        Assert.NotEqual(
            ceremonyId,
            InstallationFounderBootstrapCeremony
                .DeriveCredentialCeremonyId(OtherRootFingerprint, credential));
        // The authority parses this as a 32-hex-character ceremony id.
        Assert.True(Guid.TryParseExact(ceremonyId, "N", out _));
        // It reveals no part of the credential artifact it was derived from.
        Assert.DoesNotContain(ceremonyId, credential, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composition_Registers_The_Ceremony_And_Its_Startup_Runner()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(
                new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                    Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.db")));
        services.Configure<NodeWebClientOptions>(options => options.Enabled = false);

        services.AddInstallationFounderBootstrapCeremony(
            RootFingerprint,
            AuthorizationSeedProfile.Production,
            Path.Combine(Path.GetTempPath(), $"t360-registration-{Guid.NewGuid():N}"));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<InstallationFounderBootstrapCeremony>());
        Assert.Single(
            provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            hosted => hosted is InstallationFounderBootstrapCeremonyHostedService);
    }

    private static InstallationFounderBootstrapCeremony CreateCeremony(
        CeremonyStores stores,
        string credentialHash,
        string? username = FounderUsername) =>
        new(
            new InstallationFounderBootstrapService(stores.IdentityFactory, TimeProvider.System),
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = username,
                FounderPasswordHash = credentialHash,
            }),
            RootFingerprint,
            TimeProvider.System,
            Path.Combine(Path.GetTempPath(), $"t360-helper-{Guid.NewGuid():N}"));

    /// <summary>
    /// Adds a SECOND installation account, in the shape <c>WebJoinerAccountMinter</c> writes when an
    /// invited joiner accepts at <c>POST /api/session/account-setup-accept</c> — that account is what
    /// makes <c>Accounts</c> multi-row in production and is the live half of the defect under test.
    /// </summary>
    /// <remarks>
    /// It also adds a second <c>InstallationAccessGrantRecord</c>, which the joiner path does NOT do
    /// today — the bootstrap service is currently the only production writer of that type, and the
    /// grant the acceptance path issues is the separate tenant substrate. That row is here so the
    /// correlation-keyed lookup is proven not to rest on grant-table cardinality EITHER, ahead of a
    /// second installation-grant writer existing. Do not read it as a claim about the joiner path.
    /// </remarks>
    private static async Task AddJoinerAccountAsync(CeremonyStores stores, string username)
    {
        var now = DateTimeOffset.UtcNow;
        var accountId = Guid.NewGuid().ToString("N");
        await using var context = stores.IdentityFactory.CreateDbContext();
        context.Accounts.Add(new InstallationAccountRecord
        {
            AccountId = accountId,
            NormalizedUsername = username.ToUpperInvariant(),
            CredentialHash = MintFounderCredential("a joiner password"),
            CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
            CredentialCeremonyId = Guid.NewGuid().ToString("N"),
            CredentialVersion = 1,
            Status = InstallationAccountStatus.Active,
            SecurityVersion = 1,
            OwnerVersion = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        context.InstallationAccessGrants.Add(new InstallationAccessGrantRecord
        {
            GrantId = Guid.NewGuid().ToString("N"),
            AccountId = accountId,
            PermissionsJson = "[]",
            Status = InstallationAccessGrantStatus.Active,
            IssuerKind = "account-setup-acceptance",
            IssuerId = "installation-admin",
            AuthorizationEpoch = 1,
            OwnerVersion = 1,
            AuditCorrelationId = $"account-setup-accept/{accountId}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Runs the ceremony through its real <see cref="IHostedService"/> startup runner.</summary>
    private static async Task RunHostedCeremonyAsync(CeremonyStores stores, string credentialHash)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var hosted = new InstallationFounderBootstrapCeremonyHostedService(
            CreateCeremony(stores, credentialHash),
            loggerFactory.CreateLogger<InstallationFounderBootstrapCeremonyHostedService>());

        await hosted.StartAsync(CancellationToken.None);
    }

    /// <summary>Mints the credential exactly as the node's <c>hash-web-password</c> subcommand does.</summary>
    private static string MintFounderCredential(string password) =>
        CreateHasher<NodeWebUser>().HashPassword(NodeWebUser.Instance, password);

    private static Argon2idPasswordHasher<TUser> CreateHasher<TUser>()
        where TUser : class =>
        new(Options.Create(new Argon2idHashOptions()));

    private static async Task<InstallationAccountRecord> SingleAccountAsync(CeremonyStores stores)
    {
        await using var context = stores.IdentityFactory.CreateDbContext();
        return await context.Accounts.AsNoTracking().SingleAsync();
    }

    private static async Task AssertExactlyOneRootAsync(CeremonyStores stores)
    {
        await using var context = stores.IdentityFactory.CreateDbContext();
        Assert.Equal(1, await context.InstallationIdentities.CountAsync());
        Assert.Equal(1, await context.Accounts.CountAsync());
        Assert.Equal(1, await context.RootKeyEpochs.CountAsync());
        Assert.Equal(1, await context.InstallationAccessGrants.CountAsync());
        Assert.Equal(1, await context.AuditEnvelopes.CountAsync());
        Assert.Equal(1, await context.AuditHeads.CountAsync());
    }

    private static async Task AssertNothingWrittenAsync(CeremonyStores stores)
    {
        await using var context = stores.IdentityFactory.CreateDbContext();
        Assert.Equal(0, await context.InstallationIdentities.CountAsync());
        Assert.Equal(0, await context.Accounts.CountAsync());
        Assert.Equal(0, await context.RootKeyEpochs.CountAsync());
        Assert.Equal(0, await context.InstallationAccessGrants.CountAsync());
        Assert.Equal(0, await context.AuditEnvelopes.CountAsync());
    }

    /// <summary>
    /// Drives the real challenge route through the real antiforgery policy: issue the anonymous
    /// binding + token on one request, then present both on the credential request.
    /// </summary>
    private static async Task<RouteResponse> PostAccountChallengeAsync(
        WebAntiforgeryPolicy antiforgery,
        IWebAccountAccessChallengeIssuer issuer,
        string username,
        string password,
        WebLoginRateLimiter? loginRateLimiter = null)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        loginRateLimiter ??= new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions()),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);

        var binding = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var issueContext = new DefaultHttpContext { RequestServices = services };
        issueContext.Request.Headers.Cookie = $"{WebSessionCookieNames.AnonymousAntiforgery}={binding}";
        Assert.True(await antiforgery.IssueAnonymousAsync(issueContext));
        var token = issueContext.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Cookie = $"{WebSessionCookieNames.AnonymousAntiforgery}={binding}";
        context.Request.Headers[WebAntiforgeryPolicy.HeaderName] = token;
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var result = await AccountChallengeRoutes.IssueAsync(
            issuer,
            antiforgery,
            loginRateLimiter,
            new AccountChallengeRoutes.IssueRequest(username, password),
            context);
        await result.ExecuteAsync(context);

        body.Position = 0;
        using var reader = new StreamReader(body);
        return new RouteResponse(
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.SetCookie.ToString());
    }

    private sealed record RouteResponse(int StatusCode, string Body, string CacheControl, string SetCookie);

    private sealed class CeremonyStores : IAsyncDisposable
    {
        private readonly string _identityPath;
        private readonly string _sessionPath;

        private CeremonyStores(string identityPath, string sessionPath)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            IdentityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(identityPath);
            SessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(sessionPath);
        }

        public InstallationFounderBootstrapServiceTests.IdentityContextFactory IdentityFactory { get; }

        public WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }

        public static async Task<CeremonyStores> CreateAsync()
        {
            var stores = new CeremonyStores(
                Path.Combine(Path.GetTempPath(), $"ceremony-identity-{Guid.NewGuid():N}.db"),
                Path.Combine(Path.GetTempPath(), $"ceremony-session-{Guid.NewGuid():N}.db"));
            await using (var identity = stores.IdentityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = stores.SessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            return stores;
        }

        public async Task<InstallationAccountRecord[]> AccountsAsync()
        {
            await using var context = IdentityFactory.CreateDbContext();
            return await context.Accounts.AsNoTracking().ToArrayAsync();
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_identityPath);
            File.Delete(_sessionPath);
            return ValueTask.CompletedTask;
        }
    }
}
