using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Ticket 237: production-composed N-principal admission, attribution, and revocation.</summary>
public sealed class NPrincipalAcceptanceE2E
{
    private static readonly string FounderUsername = Environment.UserName;
    private const string FounderPassword = "ticket237 founder correct horse battery staple";
    private const string SessionToken = "ticket237-local-control-token";
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task NPrincipalAcceptance(int n)
    {
        await using var fleet = await Fleet.CreateAsync(n);
        var founder = fleet.Founder;
        var founderAttach = await founder.EnsureFounderAttachedAsync();
        Assert.True(
            founderAttach is FounderTenantMembershipAttachStatus.Attached or
                FounderTenantMembershipAttachStatus.AlreadyAttached,
            $"founder attach status: {founderAttach}; {await founder.FounderAttachDiagnosticsAsync()}");
        var tenant = ActiveTeamTenantContext.ProjectTenantId(founder.ActiveTeam.Active!.TeamId);
        var founderHandle = await LoginAsync(founder, FounderUsername, FounderPassword, tenant.Value);
        var founderPrincipal = await founder.Principals.AuthenticateAsync(founderHandle);
        Assert.NotNull(founderPrincipal);

        var actors = new List<Actor> { new(founder, founderHandle, founderPrincipal, null) };
        for (var index = 1; index < n; index++)
        {
            var username = $"ticket237-joiner-{index}";
            var password = $"ticket237 joiner {index} correct horse battery staple";
            var issued = await founder.Admin.IssueInvitationAsync(
                founderHandle,
                tenant.Value,
                [Permission.ContactsRead, Permission.SchedulingRead],
                $"ticket237-invite-{n}-{index}",
                Authority(founderPrincipal, tenant, founder.Clock.GetUtcNow()));
            Assert.NotNull(issued);

            var credential = founder.Credentials.Create(password);
            Assert.NotNull(credential);
            var accepted = await founder.Acceptance.AcceptAsync(new AccountSetupAcceptCommand(
                issued.Code, tenant.Value, username, credential.CredentialHash, credential.CredentialCeremonyId));
            Assert.Equal(AccountSetupAcceptStatus.Accepted, accepted.Status);

            var handle = await LoginAsync(founder, username, password, tenant.Value);
            var principal = await founder.Principals.AuthenticateAsync(handle);
            Assert.NotNull(principal);
            var grantId = await founder.GrantIdAsync(tenant.Value, principal.PrincipalUserId.Value);
            var pairing = founder.PairingMint.MintForSession(principal, founder.Roster.Current).Token;
            Assert.NotNull(pairing);

            var node = fleet.Joiners[index - 1];
            await using var admission = await node.StartAdmissionRouteAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, AdmissionRoutes.RouteBase + "/join")
            {
                Content = JsonContent.Create(new
                {
                    tokenId = pairing!.TokenId,
                    joiningPartyId = principal.PrincipalUserId.Value,
                    teamId = pairing.Anchor.TeamId,
                    genesisPartyId = pairing.Anchor.GenesisPartyId,
                    genesisPublicKey = pairing.Anchor.GenesisPublicKey,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
            request.Headers.Add("Idempotency-Key", $"ticket237-join-{n}-{index}");
            using var response = await admission.Client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, responseBody);
            await node.WaitForTeamAsync(founder.Roster.Current.TeamId);

            actors.Add(new Actor(node, handle, principal, grantId));
        }

        Assert.Equal(n, actors.Select(actor => actor.Principal.PrincipalUserId.Value).Distinct().Count());
        Assert.Equal(n, actors.Select(actor => actor.Node.Signer.Signer.IssuerId.ToBase64Url()).Distinct().Count());
        Assert.All(actors, actor => Assert.True(founder.Roster.Current.Contains(actor.Principal.PrincipalUserId.Value)));

        await fleet.ConvergeRosterAsync(actors.Select(actor => actor.Principal.PrincipalUserId.Value).ToArray());
        for (var index = 0; index < actors.Count; index++)
        {
            var actor = actors[index];
            actor.Node.Clock.Advance(TimeSpan.FromSeconds(1));
            await actor.Node.WriteAsync(actor.Principal, $"T237-{n}-{index}", 100m + index);
        }

        var earlierAttributions = new List<IReadOnlyList<string>>();
        foreach (var actor in actors)
            earlierAttributions.Add(await actor.Node.JournalAttributionsAsync());
        Assert.Equal(n, earlierAttributions.Sum(attributions => attributions.Count));
        Assert.Equal(
            actors.Select(actor => actor.Principal.PrincipalUserId.Value).Order(StringComparer.Ordinal),
            earlierAttributions.SelectMany(static attribution => attribution).Order(StringComparer.Ordinal));

        var revoked = actors[1];
        founder.Clock.Advance(TimeSpan.FromSeconds(1));
        var revocation = await founder.Admin.RevokeMemberGrantAsync(
            founderHandle,
            tenant.Value,
            revoked.GrantId!,
            Authority(founderPrincipal, tenant, founder.Clock.GetUtcNow()));
        Assert.NotNull(revocation);
        Assert.Equal(AdminRevokeMemberStatus.Revoked, revocation.Status);

        Assert.Null(await founder.Principals.AuthenticateAsync(revoked.SelectedHandle));
        foreach (var survivor in actors.Where(actor => !ReferenceEquals(actor, revoked)))
            Assert.NotNull(await founder.Principals.AuthenticateAsync(survivor.SelectedHandle));

        await fleet.ConvergeRevocationAsync(revoked.Principal.PrincipalUserId.Value);
        var founderTrust = founder.Worker.BoundTeam!.Services.GetRequiredService<IPeerTrustPolicy>();
        Assert.False(founderTrust.IsTrusted(Hello(revoked.Node.TeamIdentity)));
        foreach (var survivor in actors.Skip(2))
            Assert.True(founderTrust.IsTrusted(Hello(survivor.Node.TeamIdentity)));

        var joinerB = actors[2];
        var postRevocationId = $"T237-{n}-survivor";
        joinerB.Node.Clock.Advance(TimeSpan.FromSeconds(1));
        await joinerB.Node.WriteAsync(joinerB.Principal, postRevocationId, 200m + n);
        Assert.True(await joinerB.Node.ContainsJournalEntryAsync(postRevocationId));
        var receipt = await joinerB.Node.PublishJournalReceiptAsync(joinerB.Principal, postRevocationId);
        var survivorDaemon = joinerB.Node.Worker.BoundGossip!;
        survivorDaemon.AddPeer(founder.ListenEndpoint, founder.TeamIdentity.PublicKey);
        await survivorDaemon.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
        MessageCrdtState? convergedReceipt = null;
        await WaitUntilAsync(
            async () => (convergedReceipt = await founder.FindJournalReceiptAsync(receipt.MessageId)) is not null,
            "post-revocation survivor TCP journal-write receipt convergence");
        Assert.Equal(joinerB.Principal.PrincipalUserId.Value, convergedReceipt!.AuthorPartyId);
        Assert.Equal(joinerB.Node.Signer.Signer.IssuerId.ToBase64Url(), convergedReceipt.AuthorIssuerId);
        Assert.Equal(postRevocationId, convergedReceipt.Body);

        var afterRevocation = new List<IReadOnlyList<string>>();
        foreach (var actor in actors)
            afterRevocation.Add(await actor.Node.JournalAttributionsAsync());
        Assert.Equal(n + 1, afterRevocation.Sum(attributions => attributions.Count));
        for (var index = 0; index < actors.Count; index++)
            Assert.All(earlierAttributions[index], attribution =>
                Assert.Contains(attribution, afterRevocation[index]));
        Assert.Equal(joinerB.Principal.PrincipalUserId.Value, afterRevocation[2][^1]);

        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revokedPeerNodeId = PeerNodeId(revoked.Node.TeamIdentity.PublicKey);
        founder.Worker.BoundGossip!.FrameReceived += OnFounderFrame;
        try
        {
            var revokedDaemon = revoked.Node.Worker.BoundGossip!;
            revokedDaemon.AddPeer(founder.ListenEndpoint, founder.TeamIdentity.PublicKey);
            await revokedDaemon.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
            await refused.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            founder.Worker.BoundGossip!.FrameReceived -= OnFounderFrame;
        }

        for (var index = 0; index < actors.Count; index++)
            Assert.Equal(afterRevocation[index], await actors[index].Node.JournalAttributionsAsync());

        void OnFounderFrame(object? _, GossipFrameEventArgs args)
        {
            if (args.FrameType == GossipFrameType.HandshakeFailure &&
                args.ErrorCode == ErrorCode.PeerUntrusted &&
                string.Equals(args.PeerNodeId, revokedPeerNodeId, StringComparison.Ordinal))
            {
                refused.TrySetResult();
            }
        }
    }

    private static AuthorizationWriteContext Authority(
        SelectedSessionRequestPrincipal principal, TenantId tenant, DateTimeOffset at) =>
        new(new ActorId(principal.PrincipalUserId.Value), tenant, at);

    private static async Task<string> LoginAsync(
        ComposedNode node, string username, string password, string tenantId)
    {
        var challenge = (await node.Challenges.IssueAsync(username, password)).Challenge;
        Assert.NotNull(challenge);
        var selection = await node.Selection.SelectAsync(challenge.Handle, tenantId);
        Assert.NotNull(selection);
        return selection.Handle;
    }

    private static HelloMessage Hello(NodeIdentity identity) => new(
        identity.NodeIdBytes, "1", ["1"], identity.PublicKey, 0, []);

    private static string PeerNodeId(byte[] publicKey) =>
        Convert.ToHexString(publicKey.AsSpan(0, Math.Min(16, publicKey.Length))).ToLowerInvariant();

    private sealed record Actor(
        ComposedNode Node,
        string SelectedHandle,
        SelectedSessionRequestPrincipal Principal,
        string? GrantId);

    private sealed class Fleet : IAsyncDisposable
    {
        private readonly List<ComposedNode> _nodes;
        private readonly TimeSpan _convergenceBudget;

        private Fleet(List<ComposedNode> nodes, long bootMilliseconds)
        {
            _nodes = nodes;
            Founder = nodes[0];
            Joiners = nodes.Skip(1).ToArray();
            // Ticket 254b: gossip convergence costs one push per survivor per round, so the wall clock it
            // needs grows with the fleet size AND with how slow the machine is. Measured on this box for
            // (n: 5): 0.4 s quiet, 11.6-25.5 s with four concurrent suite runs and a CPU burner - against
            // a flat 30 s wall, which is why the (n: 5) row went red four times in loaded landing gates
            // and green every time alone. Fleet boot is the machine-slowness proxy this test already
            // pays (2.7 s/node quiet, 7-13 s/node loaded) and it scales with n, so budget four times the
            // observed boot with the old 30 s as a floor: on a quiet machine both rows keep exactly the
            // 30 s they had, and under load the budget rises with the same factor that slowed the fleet.
            _convergenceBudget = TimeSpan.FromMilliseconds(Math.Max(30_000, 4 * bootMilliseconds));
        }

        internal ComposedNode Founder { get; }
        internal IReadOnlyList<ComposedNode> Joiners { get; }

        internal static async Task<Fleet> CreateAsync(int count)
        {
            var founderHash = new Argon2idPasswordHasher<InstallationAccountRecord>(
                Options.Create(new Harborline.Api.Foundation.PasswordHashing.Argon2idHashOptions()))
                .HashPassword(HashSubject, FounderPassword);
            var nodes = new List<ComposedNode>();
            var bootStart = Environment.TickCount64;
            try
            {
                var founder = await ComposedNode.CreateAsync(0, founderHash, null, null);
                nodes.Add(founder);
                for (var index = 1; index < count; index++)
                    nodes.Add(await ComposedNode.CreateAsync(
                        index,
                        null,
                        () => founder.ListenEndpoint,
                        () => EnrollmentDeadlineModel.ForExchange(bootStart, Environment.TickCount64)));
                return new Fleet(nodes, Environment.TickCount64 - bootStart);
            }
            catch
            {
                foreach (var node in nodes.AsEnumerable().Reverse()) await node.DisposeAsync();
                throw;
            }
        }

        internal async Task ConvergeRosterAsync(IReadOnlyCollection<string> parties)
        {
            var founderDaemon = Founder.Worker.BoundGossip!;
            foreach (var joiner in Joiners)
                founderDaemon.AddPeer(joiner.ListenEndpoint, joiner.TeamIdentity.PublicKey);

            // Ticket 254b: the pushes used to fire ONCE, in the AddPeer loop, and the wait that followed was
            // passive. A push that reaches a joiner whose daemon is not serving yet is simply lost, and with
            // RoundIntervalSeconds=3600 the daemon never pushes again on its own - so the roster never
            // converges and no deadline can rescue the row. That is the (n: 5) red the landing gates saw and
            // the worktree never did: n-1 one-shot pushes, each racing a peer the loaded machine has not
            // scheduled yet, so four draws instead of two. Re-push on every poll, exactly as
            // ConvergeRevocationAsync already does, and the wait stops depending on machine speed.
            var deadline = Environment.TickCount64 + (long)_convergenceBudget.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                await founderDaemon.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
                if (Joiners.All(joiner => parties.All(joiner.Roster.Current.Contains))) return;
                await Task.Delay(50);
            }
            Assert.Fail(
                $"Timed out waiting for N-member roster convergence after {_convergenceBudget.TotalSeconds:0} s "
                + $"({_nodes.Count} nodes).");
        }

        internal async Task ConvergeRevocationAsync(string revokedParty)
        {
            var founderDaemon = Founder.Worker.BoundGossip!;
            var survivors = Joiners.Skip(1).ToArray();
            var deadline = Environment.TickCount64 + (long)_convergenceBudget.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                await founderDaemon.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
                if (survivors.All(joiner => !joiner.Roster.Current.Contains(revokedParty))) return;
                await Task.Delay(50);
            }
            Assert.Fail($"Timed out waiting for revocation convergence after {_convergenceBudget.TotalSeconds:0} s ({_nodes.Count} nodes).");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes.AsEnumerable().Reverse()) await node.DisposeAsync();
        }
    }

    private sealed class ComposedNode : IAsyncDisposable
    {
        private static readonly HashSet<string> StartupTypes =
        [
            "LocalNodeStoreEncryptionGuard",
            "InstallationFounderBootstrapCeremonyHostedService",
            "ContactSyncBootstrapHostedService",
            "CommsSyncBootstrapHostedService",
            "RosterSyncBootstrapHostedService",
            "MultiTeamBootstrapHostedService",
            "AuthorizationSeedHostedService",
            "FounderTenantMembershipAttachHostedService",
            "LocalNodeWorker",
        ];

        private readonly IServiceProvider _provider;
        private readonly List<IHostedService> _started;
        private readonly string _directory;

        private ComposedNode(
            IServiceProvider provider,
            List<IHostedService> started,
            string directory,
            MutableTimeProvider clock)
        {
            _provider = provider;
            _started = started;
            _directory = directory;
            Clock = clock;
        }

        internal MutableTimeProvider Clock { get; }
        internal NodeTeamRoster Roster => _provider.GetRequiredService<NodeTeamRoster>();
        internal IActiveTeamAccessor ActiveTeam => _provider.GetRequiredService<IActiveTeamAccessor>();
        internal LocalNodeWorker Worker => _provider.GetRequiredService<LocalNodeWorker>();
        internal NodePrincipalSigner Signer => _provider.GetRequiredService<NodePrincipalSigner>();
        internal NodeIdentity TeamIdentity => Worker.BoundTeam!.Services.GetRequiredService<INodeIdentityProvider>().Current;
        internal string ListenEndpoint => Assert.IsType<TcpSyncDaemonTransport>(
            Worker.BoundTeam!.Services.GetRequiredService<ISyncDaemonTransport>()).ListenEndpoint!;
        internal IWebAccountAccessChallengeIssuer Challenges =>
            _provider.GetRequiredService<IWebAccountAccessChallengeIssuer>();
        internal IWebTenantSelectionAuthority Selection =>
            _provider.GetRequiredService<IWebTenantSelectionAuthority>();
        internal IWebSelectedSessionPrincipalAuthority Principals =>
            _provider.GetRequiredService<IWebSelectedSessionPrincipalAuthority>();
        internal IAdminTeamAccessAuthority Admin => _provider.GetRequiredService<IAdminTeamAccessAuthority>();
        internal IAccountSetupAcceptanceAuthority Acceptance =>
            _provider.GetRequiredService<IAccountSetupAcceptanceAuthority>();
        internal IWebChosenCredentialFactory Credentials => _provider.GetRequiredService<IWebChosenCredentialFactory>();
        internal WebAdmittedMemberPairingTokenMint PairingMint =>
            _provider.GetRequiredService<WebAdmittedMemberPairingTokenMint>();

        internal static async Task<ComposedNode> CreateAsync(
            int index,
            string? founderHash,
            Func<string>? admitterEndpoint,
            Func<TimeSpan>? enrollBudget)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ticket237-node-{index}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var clock = new MutableTimeProvider(Start);
            IServiceProvider? provider = null;
            var args = new List<string>
            {
                "--environment=Production",
                "--LocalNode:RootSeedHex=" + SeedHex(index),
                "--LocalNode:Sync:ListenForPeers=true",
                "--LocalNode:Sync:BindAddress=tcp://127.0.0.1:0",
                "--LocalNode:Sync:NetworkTrust=Known",
                "--LocalNode:Sync:RoundIntervalSeconds=3600",
                "--LocalNode:MultiTeam:Enabled=false",
                "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                "--Logging:EventLog:LogLevel:Default=None",
                "--LocalNode:WebClient:Enabled=" + (founderHash is null ? "false" : "true"),
            };
            if (founderHash is not null)
            {
                args.Add("--LocalNode:WebClient:FounderUsername=" + FounderUsername);
                args.Add("--LocalNode:WebClient:FounderPasswordHash=" + founderHash);
            }
            if (admitterEndpoint is not null)
                args.Add("--LocalNode:Enrollment:AdmitterSyncEndpoint=" + admitterEndpoint());

            await Assert.ThrowsAsync<CompositionCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    args.ToArray(),
                    sessionTokenOverride: SessionToken,
                    dataDirectory: directory,
                    kernelClock: clock,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        // Ticket 254b: the second load-only failure mode of this row, {"error":"no_response"}
                        // on the admission join (seen on both (n: 3) and (n: 5)). Two causes, one registration.
                        // (a) The admitter address came from LocalNode:Enrollment:AdmitterSyncEndpoint, a config
                        //     string frozen when the joiner composed. The founder's gossip daemon listens on an
                        //     EPHEMERAL port and rebinds as its roster changes, so by the time joiner k dials, the
                        //     frozen address can be a port nobody is listening on - the dial fails, the transport
                        //     fails closed, and n-1 sequential joins give (n: 5) twice (n: 3)'s exposure. That is
                        //     precisely what SocketEnrollmentTransport's Func<string> ctor argument exists for
                        //     ("resolved per call so a changed peer address is picked up"); the test was the one
                        //     defeating it, so hand it the LIVE endpoint instead of a snapshot.
                        // (b) Its per-exchange deadline (dial + send + receive) defaults to 15 s, sized for a real
                        //     LAN on an idle box and not reachable from configuration; five composed hosts in one
                        //     loaded test process blow past it. Sample the fleet's elapsed composition time when
                        //     the exchange starts: a founder-only sample becomes stale when suite load arrives
                        //     after founder boot but before a later admission route uses this transport.
                        // Same production transport class, same endpoint factory shape as Program.cs - only the
                        // two machine-dependent inputs are made explicit.
                        // Follow-on: production has no operator knob for the deadline; it should get one.
                        if (admitterEndpoint is not null && enrollBudget is not null)
                            services.AddSingleton<IEnrollmentTransport>(sp => new DeferredSocketEnrollmentTransport(
                                admitterEndpoint: admitterEndpoint,
                                timeout: enrollBudget,
                                logger: sp.GetService<ILogger<SocketEnrollmentTransport>>()));
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        throw new CompositionCompleteException();
                    }));
            Assert.NotNull(provider);

            var started = new List<IHostedService>();
            try
            {
                foreach (var service in provider!.GetServices<IHostedService>())
                {
                    if (!StartupTypes.Contains(service.GetType().Name)) continue;
                    await service.StartAsync(CancellationToken.None);
                    started.Add(service);
                }
                var node = new ComposedNode(provider, started, directory, clock);
                await node.WaitForTeamAsync(node.Roster.Current.TeamId);
                return node;
            }
            catch
            {
                foreach (var service in started.AsEnumerable().Reverse())
                    await service.StopAsync(CancellationToken.None);
                await DisposeProviderAsync(provider!);
                TryDelete(directory);
                throw;
            }
        }

        internal async Task WaitForTeamAsync(Guid teamId) =>
            await WaitUntilAsync(
                () => Worker.BoundTeam?.TeamId.Value == teamId && Worker.BoundGossip is not null,
                $"worker bind for team {teamId:D}");

        internal async Task<AdmissionListener> StartAdmissionRouteAsync()
        {
            var app = new SharedHostedWebApp(
                _provider,
                _provider.GetRequiredService<IOptions<LocalNodeOptions>>(),
                _provider.GetRequiredService<LocalNodeExecutableEndpointRegistry>(),
                _provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                Clock,
                "http://127.0.0.1:0");
            var endpoint = ActivatorUtilities.CreateInstance<HostedAdmissionApiEndpoint>(_provider, app);
            await endpoint.StartAsync(CancellationToken.None);
            await app.StartAsync(CancellationToken.None);
            return new AdmissionListener(app, new HttpClient
            {
                BaseAddress = new Uri(app.SelectedUrl!),
                Timeout = TimeSpan.FromSeconds(30),
            });
        }

        internal async Task<string> GrantIdAsync(string tenantId, string principalId)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            return await context.Grants.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.SubjectId == principalId)
                .Select(row => row.GrantId)
                .SingleAsync();
        }

        internal Task<FounderTenantMembershipAttachStatus> EnsureFounderAttachedAsync() =>
            _provider.GetRequiredService<FounderTenantMembershipAttachService>()
                .RunAsync(CancellationToken.None);

        internal async Task<string> FounderAttachDiagnosticsAsync()
        {
            var searchFactory = _provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
            await using var search = await searchFactory.CreateDbContextAsync();
            var grants = await search.Grants.AsNoTracking()
                .Select(row => $"{row.SourceReference}/{row.SubjectId}/{row.RoleName}/{row.GranterKind}")
                .ToArrayAsync();
            var identityFactory = _provider
                .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
            await using var identity = await identityFactory.CreateDbContextAsync();
            var coordinators = await identity.Coordinators.AsNoTracking()
                .Select(row => $"{row.State}/{row.FailureCode}")
                .ToArrayAsync();
            return $"grants=[{string.Join(',', grants)}], markers={await identity.BootstrapClaimMarkers.CountAsync()}, " +
                   $"coordinators=[{string.Join(',', coordinators)}]";
        }

        internal async Task WriteAsync(
            SelectedSessionRequestPrincipal principal, string id, decimal amount)
        {
            var tenant = ActiveTeamTenantContext.ProjectTenantId(ActiveTeam.Active!.TeamId);
            using (NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(principal)))
            {
                var decision = TestAuthorization.AllowedDecision(
                    tenant, id, "journal-entry", TeamRolePermissions.LedgerPost,
                    principal.PrincipalUserId.Value, Clock.GetUtcNow());
                await _provider.GetRequiredService<NodeEfJournalStore>().SaveAtomicAsync(
                    tenant, PostedEntry(tenant, id, amount, Clock.GetUtcNow()), decision);
            }
        }

        internal async Task<IReadOnlyList<string>> JournalAttributionsAsync()
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            var rows = await context.Set<NodeAuditEventRow>().AsNoTracking()
                .Where(row => row.EventType == NodeAuditWriteEnlister.JournalPostedEventType)
                .OrderBy(row => row.OccurredAt).ThenBy(row => row.AuditId)
                .ToListAsync();
            var verifier = _provider.GetRequiredService<IOperationVerifier>();
            foreach (var row in rows)
            {
                var operation = NodeAuditSignaturePayload.TryReconstruct(
                    row, Signer.Signer.IssuerId, row.Signature!);
                Assert.NotNull(operation);
                Assert.True(verifier.Verify(operation!));
            }
            return rows.Select(row =>
            {
                using var document = JsonDocument.Parse(row.Payload);
                return document.RootElement.GetProperty("attribution")
                    .GetProperty("member_party_id").GetString()!;
            }).ToArray();
        }

        internal async Task<bool> ContainsJournalEntryAsync(string id)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            return await context.Set<JournalEntry>().AsNoTracking()
                .AnyAsync(entry => entry.Id == new JournalEntryId(id));
        }

        internal async Task<MessageCrdtState> PublishJournalReceiptAsync(
            SelectedSessionRequestPrincipal principal, string journalEntryId)
        {
            var tenant = ActiveTeamTenantContext.ProjectTenantId(ActiveTeam.Active!.TeamId);
            var receipt = await CommsMessageFactory.CreateSignedAsync(
                Signer.Signer,
                principal.PrincipalUserId.Value,
                tenant.Value,
                journalEntryId,
                Clock.GetUtcNow());
            var comms = _provider.GetRequiredService<CommsCrdtProjection>();
            await comms.PersistLocalAsync(receipt, CancellationToken.None);
            comms.AppendLocal(receipt);
            return receipt;
        }

        internal async Task<MessageCrdtState?> FindJournalReceiptAsync(string messageId)
        {
            var tenant = ActiveTeamTenantContext.ProjectTenantId(ActiveTeam.Active!.TeamId);
            return (await _provider.GetRequiredService<CommsCrdtProjection>()
                    .ReadLogAsync(tenant.Value, CancellationToken.None))
                .SingleOrDefault(message => message.MessageId == messageId);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in _started.AsEnumerable().Reverse())
            {
                try { await service.StopAsync(CancellationToken.None); }
                catch { /* cleanup after the test verdict */ }
            }
            await DisposeProviderAsync(_provider);
            TryDelete(_directory);
        }
    }

    private sealed class DeferredSocketEnrollmentTransport(
        Func<string> admitterEndpoint,
        Func<TimeSpan> timeout,
        ILogger<SocketEnrollmentTransport>? logger) : IEnrollmentTransport
    {
        public Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct) =>
            new SocketEnrollmentTransport(admitterEndpoint, timeout(), logger).SendAsync(request, ct);
    }

    private sealed class AdmissionListener(SharedHostedWebApp app, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private sealed class CompositionCompleteException : Exception;

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;
        public override DateTimeOffset GetUtcNow() => _current;
        internal void Advance(TimeSpan by) => _current += by;
    }

    private static readonly InstallationAccountRecord HashSubject = new()
    {
        AccountId = "ticket237-hash-subject",
        NormalizedUsername = "TICKET237-HASH-SUBJECT",
        CredentialHash = "not-persisted",
        CredentialAlgorithm = "not-persisted",
        CredentialCeremonyId = "not-persisted",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Active,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = Start,
        UpdatedAtUtc = Start,
    };

    private static JournalEntry PostedEntry(
        TenantId tenant, string id, decimal amount, DateTimeOffset at) =>
        new(
            new JournalEntryId(id),
            tenant,
            DateOnly.FromDateTime(at.UtcDateTime),
            "ticket 237 N-principal acceptance",
            [
                new JournalEntryLine(new GLAccountId("1000"), amount, 0m),
                new JournalEntryLine(new GLAccountId("4000"), 0m, amount),
            ],
            new Instant(at))
        {
            Status = JournalEntryStatus.Posted,
            PostedAtUtc = new Instant(at),
        };

    private static string SeedHex(int index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"ticket237-root-{index}")));

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for " + because + ".");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for " + because + ".");
    }

    private static async ValueTask DisposeProviderAsync(IServiceProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (provider is IDisposable disposable) disposable.Dispose();
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* cleanup after the test verdict */ }
    }
}
