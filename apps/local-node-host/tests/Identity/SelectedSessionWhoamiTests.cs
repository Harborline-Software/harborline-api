using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3329 step 1 — the selected-audience whoami, driven over the REAL production listener.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> Kestrel + the production <see cref="SharedHostedWebApp"/> caller-auth gate,
/// the real <see cref="WebSelectedSessionPrincipalAuthority"/> materializing the request principal
/// from a durable session row, the real <see cref="SelectedSessionIdentityRoutes"/> handler, the real
/// <see cref="WebSelectedSessionIdentityAuthority"/>, the real installation-identity store (migrated
/// SQLite) for the root designation, and the real People join — a durable
/// <see cref="NodeEfPartyRepository"/> behind the real
/// <see cref="NodeEfSelectedSessionMemberLabelReader"/>. The missing-name case below is produced by
/// removing the Party binding from that real store, not by a reader stubbed to return null.
/// </para>
/// <para>
/// <b>What is substituted, and why it is not the seam under test.</b> The tenant-membership store,
/// its partition resolver, its admission and its lease coordinator (the fence is proven by
/// <c>Mtw2TwoUserAcceptanceE2E</c> and <c>SelectedSessionRequestPrincipalTests</c>; re-driving it here
/// would add a second identity substrate without touching this route), the org-branding store, and
/// the v1 <see cref="INodeWebSessionAuthority"/>. That last one is deliberately made MAXIMALLY
/// PERMISSIVE — it admits any request carrying its cookie — because the audience proof is strongest
/// when a fully authenticated v1 session still reads nothing here.
/// </para>
/// <para>The clock is frozen; nothing below depends on elapsed time.</para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3329")]
public sealed class SelectedSessionWhoamiTests
{
    private const string CallerToken = "selected-whoami-caller-token";

    private const string FounderHandle = "founder-handle-with-at-least-256-bits-of-test-entropy-0000";
    private const string MemberHandle = "member-handle-with-at-least-256-bits-of-test-entropy-00000";
    private const string LegacyHandle = "legacy-handle-with-at-least-256-bits-of-test-entropy-00000";

    private const string FounderAccountId = "account-founder";
    private const string MemberAccountId = "account-member";
    private const string FounderParty = "party-founder";
    private const string MemberParty = "party-member";
    private const string FounderPrincipal = "principal-founder";
    private const string MemberPrincipal = "principal-member";

    private const string TenantLabel = "Harbor Lighting Co.";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 18, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IdleExpiry = Now.AddMinutes(20);
    private static readonly DateTimeOffset AbsoluteExpiry = Now.AddHours(8);

    [Fact(DisplayName =
        "Whoami carries identity: account, Party, People name, tenant + its label, founder standing, " +
        "and an expiry named advisory — and carries no revalidation machinery")]
    public async Task Whoami_Carries_Identity_And_Nothing_Else()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        using var response = await fixture.WhoamiWithSelectedAsync(FounderHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(FounderAccountId, root.GetProperty("accountId").GetString());
        Assert.Equal(FounderParty, root.GetProperty("partyId").GetString());
        Assert.Equal("Ada Founder", root.GetProperty("displayName").GetString());
        Assert.Equal(fixture.TenantId, root.GetProperty("tenant").GetProperty("id").GetString());
        Assert.Equal(TenantLabel, root.GetProperty("tenant").GetProperty("displayName").GetString());
        Assert.Equal("founder", root.GetProperty("standing").GetString());

        // A direct route fixture has no request-scoped PEP snapshot. Unresolved is a distinct wire
        // state; it must never collapse into an empty permission set that the Harborline App can render as
        // "no permissions".
        Assert.True(root.TryGetProperty("permissions", out var permissions));
        Assert.Equal(JsonValueKind.Null, permissions.ValueKind);

        // The session dies at the EARLIER of its two ceilings, so that is what a client may warn on.
        // Read from the durable row rather than a constant: the gate slides the idle TTL on every
        // admitted request, which is itself why the value is advisory — it moves under the client.
        var row = await fixture.ReadSessionAsync(FounderHandle);
        Assert.True(
            row.IdleExpiresAtUtc < row.AbsoluteExpiresAtUtc,
            "the fixture must leave the idle ceiling the earlier one, or this proves nothing");
        Assert.Equal(row.IdleExpiresAtUtc, root.GetProperty("advisoryExpiresAtUtc").GetDateTimeOffset());
        Assert.NotEqual(row.AbsoluteExpiresAtUtc, root.GetProperty("advisoryExpiresAtUtc").GetDateTimeOffset());

        // Ruling 2, asserted as a property of the payload rather than a promise in a comment: the
        // response names identity plus the server-derived effective permission snapshot. Owner
        // versions, epochs and membership ids are revalidation machinery — they change under the
        // client, and on the wire they invite it to cache them.
        var fields = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "accountId", "partyId", "displayName", "tenant", "standing", "advisoryExpiresAtUtc",
                "permissions",
            },
            fields);
        foreach (var forbidden in new[]
        {
            "ownerVersion", "OwnerVersion", "epoch", "Epoch", "membership", "Membership",
            "grant", "Grant", "correlation", "Correlation",
        })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName =
        "A member's standing is member, sourced from the installation's root designation — not a constant")]
    public async Task Standing_Distinguishes_The_Member_From_The_Founder()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        using var founder = await fixture.WhoamiWithSelectedAsync(FounderHandle);
        using var member = await fixture.WhoamiWithSelectedAsync(MemberHandle);

        Assert.Equal(HttpStatusCode.OK, founder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal("founder", await ReadStringAsync(founder, "standing"));
        Assert.Equal("member", await ReadStringAsync(member, "standing"));

        // The two sessions describe two different people on one installation.
        Assert.Equal("Ada Founder", await ReadStringAsync(founder, "displayName"));
        Assert.Equal("Grace Member", await ReadStringAsync(member, "displayName"));
        Assert.Equal(MemberAccountId, await ReadStringAsync(member, "accountId"));
    }

    [Fact(DisplayName =
        "Without a root designation the standing is unresolved for everyone — the founder is never " +
        "labelled a member because the installation has not answered the question")]
    public async Task Standing_Is_Unresolved_Until_The_Installation_Designates_A_Root()
    {
        await using var fixture = await Fixture.CreateAsync();

        // No DesignateRootAsync, and this fixture seeds no designation row -- so it models an
        // installation bootstrapped BEFORE earlier repository ticket #3373. Since that change the real bootstrap
        // ceremony writes the designation itself, so a genuinely fresh install now reads Founder
        // rather than Unresolved. This test pins the remaining reachable Unresolved case: a
        // pre-3373 install, which nothing backfills.
        using var founder = await fixture.WhoamiWithSelectedAsync(FounderHandle);
        using var member = await fixture.WhoamiWithSelectedAsync(MemberHandle);

        Assert.Equal(HttpStatusCode.OK, founder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal("unresolved", await ReadStringAsync(founder, "standing"));
        Assert.Equal("unresolved", await ReadStringAsync(member, "standing"));

        // Both sessions are still fully identified. An unanswerable standing costs the standing, not
        // the identity.
        Assert.Equal(FounderAccountId, await ReadStringAsync(founder, "accountId"));
        Assert.Equal("Ada Founder", await ReadStringAsync(founder, "displayName"));
    }

    [Fact(DisplayName =
        "An unresolvable People binding leaves the NAME absent and the session valid — it never " +
        "substitutes a founder, an operator, or a generic local user")]
    public async Task Missing_Party_Leaves_The_Name_Absent_And_The_Session_Valid()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        // Remove the member's People binding from the REAL store — an archived party, a mid-sync gap,
        // a membership whose party was removed. The session row is untouched.
        await fixture.DetachPartyBindingAsync(MemberPrincipal);

        using var response = await fixture.WhoamiWithSelectedAsync(MemberHandle);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // The specific failure this guards: no borrowed identity reaches the one surface a human
        // reads. Ada is the founder's REAL name and lives in the same store, so a fallback that
        // borrowed it would be a live human misidentified to another.
        //
        // Scanned over the WHOLE response body and asserted BEFORE the null check below, deliberately.
        // Scoping this to displayName after asserting displayName is null made it unfireable — it was
        // checking a value the previous line had already proved absent, so it never once ran against
        // real content. Over the whole body it also catches a substitute landing in any other field.
        foreach (var borrowed in new[]
        {
            "Ada Founder", "founder", "Founder", "operator", "Operator", "local", "Local", "Unknown",
        })
        {
            Assert.DoesNotContain(borrowed, body, StringComparison.Ordinal);
        }

        // The session is REAL and fully identified; only its label did not resolve. That is a
        // legible condition. A session wearing the wrong label is not.
        Assert.Equal(MemberAccountId, root.GetProperty("accountId").GetString());
        Assert.Equal(MemberParty, root.GetProperty("partyId").GetString());
        Assert.Equal("member", root.GetProperty("standing").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("displayName").ValueKind);
    }

    [Fact(DisplayName =
        "Audience isolation: a fully authenticated v1 legacy session reads nothing here and never " +
        "reaches the selected authority, while the selected audience is served normally")]
    public async Task A_Legacy_Handle_Never_Reaches_The_Selected_Authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        // The WORKING path first, so a route that answered nothing at all could not pass this test
        // by refusing everything.
        using var selected = await fixture.WhoamiWithSelectedAsync(FounderHandle);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(FounderAccountId, await ReadStringAsync(selected, "accountId"));

        // Now the same endpoint with a LIVE v1 legacy handle and no selected cookie. The legacy
        // authority admits it at the listener, so this request genuinely reaches the handler.
        using var legacy = await fixture.WhoamiWithLegacyAsync(LegacyHandle);

        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        Assert.Equal("no_selected_session", await ReadStringAsync(legacy, "error"));

        // The 401 came from the HANDLER, not from the gate refusing to admit the request: the legacy
        // authority was consulted and said yes. Without this the refusal above could be the listener
        // rejecting an unauthenticated caller, which is a different (and weaker) property.
        Assert.Equal(1, fixture.LegacyAdmissions);

        // THE property. The selected authority was consulted exactly once — for the selected handle.
        // The legacy handle never reached it, so it could neither read this surface nor enumerate
        // through it. The recorder additionally THROWS on any handle it does not know, so arriving
        // here at all is itself part of the proof.
        Assert.Equal(new[] { FounderHandle }, fixture.SelectedAuthorityCalls);
    }

    [Fact(DisplayName =
        "A People label resolving to a DIFFERENT Party than the session's is dropped — the one path " +
        "by which a real other person's name could reach the identity surface")]
    public async Task A_Label_For_Another_Party_Is_Never_Borrowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);
        var founderPrincipal = await fixture.AuthenticateAsync(FounderHandle);

        // A label reader whose join has diverged from the session's Party — the principal is Ada, the
        // People layer now answers with GRACE, a real person who exists on this installation. The two
        // readers are separately editable types, so nothing structural keeps them agreeing; the gate's
        // own Party check is upstream of this authority and a second caller would not carry it.
        var diverged = fixture.IdentityAuthorityWith(new FixedLabelReader(
            new SelectedSessionMemberLabel(new CanonicalPartyReference(MemberParty), "Grace Member")));

        var identity = await diverged.DescribeAsync(FounderHandle, founderPrincipal);

        // The session stays valid and stays Ada's. The name is dropped, not swapped.
        Assert.NotNull(identity);
        Assert.Equal(FounderAccountId, identity!.AccountId);
        Assert.Equal(FounderParty, identity.PartyId.Value);
        Assert.Null(identity.DisplayName);
        Assert.NotEqual("Grace Member", identity.DisplayName);
    }

    [Fact(DisplayName =
        "A designated but UNVERIFIED root reads unresolved — the same readability predicate the " +
        "installation's own cutover uses, so standing never answers more specifically than it")]
    public async Task An_Unverified_Designation_Does_Not_Confer_Standing()
    {
        await using var fixture = await Fixture.CreateAsync();

        // InstallationIdentityCutoverOrchestrator.HasReadableV2CandidateAsync refuses a designation
        // whose VerifiedAtUtc is null. Reading standing off the account id alone would answer founder
        // here while the installation answers not-readable.
        await fixture.DesignateRootAsync(FounderAccountId, verified: false);

        using var founder = await fixture.WhoamiWithSelectedAsync(FounderHandle);

        Assert.Equal(HttpStatusCode.OK, founder.StatusCode);
        Assert.Equal("unresolved", await ReadStringAsync(founder, "standing"));
        Assert.Equal(FounderAccountId, await ReadStringAsync(founder, "accountId"));
    }

    [Fact(DisplayName =
        "Audience isolation holds for the v1 BEARER transport too, not just its cookie")]
    public async Task A_Legacy_Bearer_Never_Reaches_The_Selected_Authority()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        // The working path first, for the same reason as the cookie test.
        using var selected = await fixture.WhoamiWithSelectedAsync(FounderHandle);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

        // The v1 authority accepts EITHER transport (a bearer header or its cookie). The cookie half
        // is covered above; this drives the bearer half, which reaches the gate by a different branch.
        using var bearer = await fixture.WhoamiWithLegacyBearerAsync(LegacyHandle);

        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        Assert.Equal("no_selected_session", await ReadStringAsync(bearer, "error"));
        Assert.Equal(1, fixture.LegacyAdmissions);
        Assert.Equal(new[] { FounderHandle }, fixture.SelectedAuthorityCalls);
    }

    [Fact(DisplayName =
        "A handle that does not name the presented principal's session is refused, not described")]
    public async Task A_Handle_That_Names_Another_Session_Is_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DesignateRootAsync(FounderAccountId);

        // Both handles name real live sessions, and each is describable on its own.
        var founderPrincipal = await fixture.AuthenticateAsync(FounderHandle);
        Assert.NotNull(await fixture.Identity.DescribeAsync(FounderHandle, founderPrincipal));

        // Pairing the founder's principal with the MEMBER's handle must refuse rather than describe a
        // mismatched pair. The real route cannot produce this pairing — the gate and the handler read
        // the same cookie — so it is driven directly. The check exists because nothing structural
        // forces the two arguments to agree once this authority is reachable from a second caller.
        Assert.Null(await fixture.Identity.DescribeAsync(MemberHandle, founderPrincipal));

        // A handle no session owns is likewise refused, not described from the principal alone.
        Assert.Null(await fixture.Identity.DescribeAsync(LegacyHandle, founderPrincipal));
    }

    private static async Task<string?> ReadStringAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(property).GetString();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _outerProvider;
        private readonly ServiceProvider _peopleProvider;
        private readonly SharedHostedWebApp _app;
        private readonly InstallationFounderBootstrapServiceTests.IdentityContextFactory _identityFactory;
        private readonly RecordingIdentityAuthority _recorder;
        private readonly PermissiveLegacyAuthority _legacy;
        private readonly IWebSelectedSessionPrincipalAuthority _principals;
        private readonly WebAccountAccessChallengeIssuerTests.SessionContextFactory _sessionFactory;
        private readonly WebSelectedSessionStore _sessionStore;
        private readonly IOrgBrandingStore _branding;

        private Fixture(
            string directory,
            ServiceProvider outerProvider,
            ServiceProvider peopleProvider,
            SharedHostedWebApp app,
            HttpClient client,
            InstallationFounderBootstrapServiceTests.IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            RecordingIdentityAuthority recorder,
            PermissiveLegacyAuthority legacy,
            IWebSelectedSessionPrincipalAuthority principals,
            IWebSelectedSessionIdentityAuthority identity,
            WebSelectedSessionStore sessionStore,
            IOrgBrandingStore branding,
            string tenantId)
        {
            _sessionFactory = sessionFactory;
            _sessionStore = sessionStore;
            _branding = branding;
            _directory = directory;
            _outerProvider = outerProvider;
            _peopleProvider = peopleProvider;
            _app = app;
            Client = client;
            _identityFactory = identityFactory;
            _recorder = recorder;
            _legacy = legacy;
            _principals = principals;
            Identity = identity;
            TenantId = tenantId;
        }

        internal HttpClient Client { get; }

        /// <summary>The real identity authority, for the contract checks the route cannot produce.</summary>
        internal IWebSelectedSessionIdentityAuthority Identity { get; }

        internal string TenantId { get; }

        /// <summary>Every handle the selected-session identity authority was consulted for, in order.</summary>
        internal IReadOnlyList<string> SelectedAuthorityCalls => _recorder.Handles;

        /// <summary>How many requests the v1 legacy authority admitted at the listener gate.</summary>
        internal int LegacyAdmissions => _legacy.Admissions;

        internal static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), $"selected-whoami-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var tenantId = Guid.NewGuid().ToString("D");

            var identityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                Path.Combine(directory, "installation-identity.db"));
            var sessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(
                Path.Combine(directory, "web-session.db"));

            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
                identity.Accounts.AddRange(
                    Account(FounderAccountId, "ADA"),
                    Account(MemberAccountId, "GRACE"));
                await identity.SaveChangesAsync();
            }

            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.AddRange(
                    Session(FounderHandle, "session-founder", FounderAccountId, tenantId,
                        "membership-founder", FounderPrincipal, FounderParty, "grant-founder"),
                    Session(MemberHandle, "session-member", MemberAccountId, tenantId,
                        "membership-member", MemberPrincipal, MemberParty, "grant-member"));
                await sessions.SaveChangesAsync();
            }

            // The REAL People store behind the REAL label reader.
            var people = new ServiceCollection();
            people.AddTestKernelClock();
            people.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
            people.AddDbContextFactory<LocalNodeDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(directory, "local-node.db")};Pooling=False"));
            people.AddNodeContacts();
            var peopleProvider = people.BuildServiceProvider();
            await using (var context = await peopleProvider
                .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()
                .CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }
            await SeedPartyAsync(peopleProvider, tenantId, FounderParty, FounderPrincipal, "Ada Founder");
            await SeedPartyAsync(peopleProvider, tenantId, MemberParty, MemberPrincipal, "Grace Member");

            var memberships = new FixedMembershipStore(
                tenantId,
                [
                    Membership(tenantId, "membership-founder", FounderAccountId, FounderPrincipal,
                        "grant-founder"),
                    Membership(tenantId, "membership-member", MemberAccountId, MemberPrincipal,
                        "grant-member"),
                ]);
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                new FixedPartitionResolver(new TenantIdentityAuthorityPartition(
                    tenantId, memberships, new UnusedLeaseCoordinator())),
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true));

            var sessionStore = new WebSelectedSessionStore(sessionFactory);
            var principalAuthority = new WebSelectedSessionPrincipalAuthority(
                sessionStore,
                identityFactory,
                coordinator,
                new FixedPartyReader(tenantId),
                Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()),
                new FixedTimeProvider(Now));

            var branding = new FixedBrandingStore(tenantId, TenantLabel);
            var identityAuthority = new WebSelectedSessionIdentityAuthority(
                sessionStore,
                identityFactory,
                new NodeEfSelectedSessionMemberLabelReader(
                    peopleProvider.GetRequiredService<NodeEfPartyRepository>()),
                branding);
            var recorder = new RecordingIdentityAuthority(identityAuthority);
            var legacy = new PermissiveLegacyAuthority();

            var outer = new ServiceCollection();
            outer.AddTestKernelClock();
            outer.AddLogging();
            outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(principalAuthority);
            outer.AddSingleton<INodeWebSessionAuthority>(legacy);
            var outerProvider = outer.BuildServiceProvider();

            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            // The production registrar's own mapping call — not a probe route.
            app.MapApiRoutes(routes => SelectedSessionIdentityRoutes.Map(
                routes.MapSelectedSessionProductGroup(),
                recorder));

            await app.StartAsync(CancellationToken.None);
            return new Fixture(
                directory,
                outerProvider,
                peopleProvider,
                app,
                new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) },
                identityFactory,
                sessionFactory,
                recorder,
                legacy,
                principalAuthority,
                identityAuthority,
                sessionStore,
                branding,
                tenantId);
        }

        internal async Task<HttpResponseMessage> WhoamiWithSelectedAsync(string handle)
        {
            using var request = Whoami();
            request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={handle}");
            return await Client.SendAsync(request);
        }

        /// <summary>A v1 legacy session cookie and nothing else — the audience the gate admits at Accept 3.</summary>
        internal async Task<HttpResponseMessage> WhoamiWithLegacyAsync(string handle)
        {
            using var request = Whoami();
            request.Headers.Add("Cookie", $"{NodeWebSessionAuthority.SessionCookieName}={handle}");
            return await Client.SendAsync(request);
        }

        /// <summary>
        /// The v1 authority's OTHER transport — an Authorization bearer. It is not the bootstrap
        /// caller token, so the gate's Accept 1 declines it and it reaches Accept 3.
        /// </summary>
        internal async Task<HttpResponseMessage> WhoamiWithLegacyBearerAsync(string handle)
        {
            using var request = Whoami();
            request.Headers.Add("Authorization", $"Bearer {handle}");
            return await Client.SendAsync(request);
        }

        /// <summary>
        /// The real authority over the same real stores, with a substituted People label reader. Used
        /// to reach the Party-mismatch guard, which the route itself cannot exercise: the listener
        /// gate's own Party check sits upstream and refuses first.
        /// </summary>
        internal IWebSelectedSessionIdentityAuthority IdentityAuthorityWith(
            ISelectedSessionMemberLabelReader labels) =>
            new WebSelectedSessionIdentityAuthority(
                _sessionStore, _identityFactory, labels, _branding);

        /// <summary>The durable session row as it stands now (the gate slides its idle ceiling).</summary>
        internal async Task<WebUserSessionRecord> ReadSessionAsync(string handle)
        {
            await using var sessions = _sessionFactory.CreateDbContext();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
            return await sessions.UserSessions.AsNoTracking()
                .SingleAsync(row => row.HandleDigest == digest);
        }

        /// <summary>Materializes a request principal the way the listener gate does.</summary>
        internal async Task<SelectedSessionRequestPrincipal> AuthenticateAsync(string handle)
        {
            var principal = await _principals.AuthenticateAsync(handle);
            Assert.NotNull(principal);
            return principal!;
        }

        /// <summary>
        /// Writes the installation's initial-root designation for one account. Pass
        /// <paramref name="verified"/> false for the designated-but-unverified row the installation's
        /// own readability predicate refuses.
        /// </summary>
        internal async Task DesignateRootAsync(string accountId, bool verified = true)
        {
            await using var identity = _identityFactory.CreateDbContext();
            identity.RootDesignations.Add(new InstallationIdentityRootDesignationRecord
            {
                SingletonKey = InstallationIdentityRootDesignationRecord.SingletonKeyValue,
                DesignationId = "designation-fixture",
                SourceCompositeKeyDigest = "digest-fixture",
                AccountId = accountId,
                ExpectedSourceVersion = 1,
                IdempotencyKeyDigest = "idempotency-fixture",
                AuditCorrelationId = "audit-fixture",
                OwnerVersion = 1,
                DesignatedAtUtc = Now,
                VerifiedAtUtc = verified ? Now : null,
            });
            await identity.SaveChangesAsync();
        }

        /// <summary>Ends the People role binding so the principal-to-Party join no longer resolves.</summary>
        internal async Task DetachPartyBindingAsync(string principalUserId)
        {
            var factory = _peopleProvider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            var role = await context.Set<PartyRole>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(row =>
                    row.RoleName == NodeEfPartyRepository.PrincipalUserBindingRoleName &&
                    row.RoleRecordId == principalUserId);
            context.Set<PartyRole>().Update(
                role.End(new Instant(Now), "fixture detach", new PartyId("fixture-actor")));
            await context.SaveChangesAsync();
        }

        private static HttpRequestMessage Whoami() =>
            new(HttpMethod.Get, SelectedSessionIdentityRoutes.WhoamiPath);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            await _peopleProvider.DisposeAsync();
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Records every handle the real authority is consulted for, and REFUSES to be consulted for a
    /// handle no test session owns. Reaching the end of the audience test is therefore itself part
    /// of the proof that the legacy handle never arrived here.
    /// </summary>
    private sealed class RecordingIdentityAuthority(IWebSelectedSessionIdentityAuthority inner)
        : IWebSelectedSessionIdentityAuthority
    {
        private readonly ConcurrentQueue<string> _handles = new();

        internal IReadOnlyList<string> Handles => _handles.ToArray();

        public Task<SelectedSessionIdentity?> DescribeAsync(
            string? selectedHandle,
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            if (selectedHandle is not (FounderHandle or MemberHandle))
            {
                throw new InvalidOperationException(
                    $"the selected-session identity authority was reached with a foreign-audience " +
                    $"handle: '{selectedHandle}'");
            }

            _handles.Enqueue(selectedHandle);
            return inner.DescribeAsync(selectedHandle, principal, cancellationToken);
        }
    }

    /// <summary>
    /// A v1 web-session authority that admits ANY request carrying its cookie. Deliberately maximally
    /// permissive: the audience proof is that even a fully authenticated v1 session reads nothing on
    /// the selected surface.
    /// </summary>
    private sealed class PermissiveLegacyAuthority : INodeWebSessionAuthority
    {
        private int _admissions;

        internal int Admissions => Volatile.Read(ref _admissions);

        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context)
        {
            // The real v1 authority is dual-accept: an HttpOnly session cookie OR an Authorization
            // bearer. Both are modelled so neither transport's branch goes unexercised.
            var present =
                context.Request.Cookies.ContainsKey(NodeWebSessionAuthority.SessionCookieName) ||
                context.Request.Headers.Authorization.ToString()
                    .StartsWith("Bearer ", StringComparison.Ordinal);
            if (present)
            {
                Interlocked.Increment(ref _admissions);
            }
            return Task.FromResult(present);
        }

        public Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) =>
            throw new NotSupportedException();
        public void IssueSessionCookie(HttpContext context, WebLoginResult login) =>
            throw new NotSupportedException();
        public void ClearSessionCookie(HttpContext context) => throw new NotSupportedException();
        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>A People label reader whose join has diverged from the session's Party.</summary>
    private sealed class FixedLabelReader(SelectedSessionMemberLabel label)
        : ISelectedSessionMemberLabelReader
    {
        public Task<SelectedSessionMemberLabel?> ReadAsync(
            TenantId tenant,
            PrincipalUserId principal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedSessionMemberLabel?>(label);
    }

    private sealed class FixedBrandingStore(string tenantId, string displayName) : IOrgBrandingStore
    {
        public Task<OrgBrandingProfile?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<OrgBrandingProfile?>(
                tenant.Value == tenantId
                    ? new OrgBrandingProfile(tenantId, displayName, null, null, null, null, Now, "fixture")
                    : null);

        public Task UpsertAsync(OrgBrandingProfile profile, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task SeedPartyAsync(
        ServiceProvider provider,
        string tenantId,
        string partyId,
        string principalUserId,
        string displayName)
    {
        var tenant = new TenantId(tenantId);
        var actor = new PartyId("fixture-actor");
        var party = Party.Create(
            tenant, PartyKind.Person, displayName, actor, new Instant(Now), new PartyId(partyId));
        var role = PartyRole.Create(
            tenant,
            party.Id,
            NodeEfPartyRepository.PrincipalUserBindingRoleName,
            principalUserId,
            actor,
            new Instant(Now));

        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        context.Set<Party>().Add(party);
        context.Set<PartyRole>().Add(role);
        await context.SaveChangesAsync();
    }

    private static InstallationAccountRecord Account(string accountId, string username) => new()
    {
        AccountId = accountId,
        NormalizedUsername = username,
        CredentialHash = "fixture-hash",
        CredentialAlgorithm = "argon2id",
        CredentialCeremonyId = $"ceremony-{accountId}",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Active,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private static WebUserSessionRecord Session(
        string handle,
        string correlation,
        string accountId,
        string tenantId,
        string membershipId,
        string principalId,
        string partyId,
        string grantId) =>
        new(
            SessionCorrelationId: correlation,
            AccountId: accountId,
            AccountSecurityVersion: 1,
            TenantId: tenantId,
            MembershipId: membershipId,
            MembershipOwnerVersion: 3,
            TenantPrincipalId: principalId,
            CanonicalPartyReference: partyId,
            PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion(grantId, 4)],
            AuthorizationEpoch: 7,
            HandleDigest: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle))),
            AntiforgeryStateId: $"antiforgery-{correlation}",
            CoordinationCorrelationId: $"coordination-{correlation}",
            IssuedAtUtc: Now.AddMinutes(-10),
            IdleExpiresAtUtc: IdleExpiry,
            AbsoluteExpiresAtUtc: AbsoluteExpiry,
            OwnerVersion: 1);

    private static TenantMembershipSnapshot Membership(
        string tenantId,
        string membershipId,
        string accountId,
        string principalId,
        string grantId) =>
        new(
            membershipId,
            accountId,
            tenantId,
            principalId,
            grantId,
            GrantOwnerVersion: 4,
            AuthorizationEpoch: 7,
            TenantMembershipStatus.Active,
            OwnerVersion: 3);

    private sealed class FixedMembershipStore : ITenantMembershipAuthorityStore
    {
        private readonly IReadOnlyDictionary<string, TenantMembershipSnapshot> _memberships;

        internal FixedMembershipStore(
            string tenantId,
            IReadOnlyList<TenantMembershipSnapshot> memberships)
        {
            TenantId = tenantId;
            _memberships = memberships.ToDictionary(row => row.AccountId, StringComparer.Ordinal);
        }

        public string TenantId { get; }

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_memberships.TryGetValue(accountId, out var row) ? row : null);

        public Task<bool> IsAdmissionBlockedAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task PrepareAsync(string correlationId, string commandFingerprint, string accountId,
            string actorAccountId, string authorityEvidenceDigest, TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<TenantMembershipIntentState?>(null);
        public Task PrepareSessionSelectionAsync(string correlationId, string commandFingerprint,
            string accountId, string membershipId, string payloadDigest,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionSelectionAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task PrepareSessionRevocationAsync(string correlationId, string commandFingerprint,
            string accountId, string membershipId, string sessionCorrelationId, string payloadDigest,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionRevocationAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedPartitionResolver(TenantIdentityAuthorityPartition partition)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken) => Task.FromResult(partition);
    }

    private sealed class FixedPartyReader(string tenantId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default)
        {
            if (tenant.Value != tenantId)
            {
                return ValueTask.FromResult<CanonicalPartyBinding?>(null);
            }
            var party = user.Value switch
            {
                FounderPrincipal => FounderParty,
                MemberPrincipal => MemberParty,
                _ => null,
            };
            return ValueTask.FromResult<CanonicalPartyBinding?>(party is null
                ? null
                : new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(party)));
        }
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(string actorAccountId, string authorityEvidenceDigest,
            string accountId, TenantMembershipMutation mutation,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateExistingAsync(string accountId, TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class UnusedLeaseCoordinator : ILeaseCoordinator
    {
        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ReleaseAsync(Lease lease, CancellationToken ct) => throw new NotSupportedException();
        public bool Holds(string resourceId) => false;
        public IReadOnlyCollection<Lease> HeldLeases => Array.Empty<Lease>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
