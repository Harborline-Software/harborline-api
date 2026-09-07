using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Buckets.DependencyInjection;
using Harborline.Api.Kernel.Events.DependencyInjection;
using Harborline.Api.Kernel.Lease.DependencyInjection;
using Harborline.Api.Kernel.Ledger.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Notifications;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Network;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Runtime.Teams;

/// <summary>
/// Factory for the stock per-team <see cref="TeamServiceRegistrar"/> that
/// <c>AddHarborlineMultiTeam</c> invokes when materializing a fresh
/// <see cref="TeamContext"/>. Wave 6.3.A lands the scaffold shell; Waves 6.3.B,
/// 6.3.C, and 6.3.D fill in the ledger trio, the sync pair, and the bucket
/// registry respectively. 6.3.E remains pending (composition-root rewire).
/// <list type="bullet">
///   <item><description>Wave 6.3.B — ledger trio: <c>IEventLog</c>,
///     <c>IQuarantineQueue</c>, <c>IEncryptedStore</c> — LANDED.</description></item>
///   <item><description>Wave 6.3.C — sync pair: per-team
///     <c>INodeIdentityProvider</c>, <c>ISyncDaemonTransport</c>,
///     <c>IGossipDaemon</c>, <c>ILeaseCoordinator</c> — LANDED.</description></item>
///   <item><description>Wave 6.3.D — <c>IBucketRegistry</c> + manifest
///     loader bound to <see cref="TeamPaths.BucketsDirectory"/> — LANDED.</description></item>
///   <item><description>Wave 6.3.E — <c>local-node-host</c> composition-root
///     rewire + <c>AddHarborlineDefaultTeamRegistrar</c> sugar — PENDING.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The composition-root helper <c>AddHarborlineDefaultTeamRegistrar</c> is not
/// shipped in 6.3.A/B/C/D — callers wire
/// <c>AddHarborlineMultiTeam(DefaultTeamServiceRegistrar.Compose(...))</c>
/// themselves. The extension-method sugar is bundled with the
/// <c>local-node-host</c> composition-root rewire in Wave 6.3.E.
/// </para>
/// </remarks>
public static class DefaultTeamServiceRegistrar
{
    /// <summary>
    /// Compose the per-team service registration callback for
    /// <c>AddHarborlineMultiTeam</c>. Fills in the ledger trio (event log,
    /// quarantine queue, encrypted store), the sync pair (team-scoped node
    /// identity, per-team transport endpoint, gossip daemon, lease
    /// coordinator), and the bucket registry.
    /// </summary>
    /// <param name="dataDirectory">Install-level data directory that
    /// <see cref="TeamPaths"/> combines with each team id to produce the
    /// per-team SQLCipher DB path, event-log directory, bucket-manifest
    /// directory, and transport endpoint. Captured in the returned closure
    /// and passed through to the per-team registrations.</param>
    /// <param name="subkeyDerivation">The installed
    /// <see cref="ITeamSubkeyDerivation"/> — used to derive a team-scoped
    /// Ed25519 keypair from the root identity (via
    /// <see cref="TeamScopedNodeIdentity.Derive(NodeIdentity, string, ITeamSubkeyDerivation)"/>)
    /// before the per-team <see cref="INodeIdentityProvider"/> is registered
    /// (Wave 6.3.C).</param>
    /// <param name="rootIdentity">The install's root Ed25519 identity
    /// (<see cref="NodeIdentity.NodeId"/> + raw 32-byte public key + raw 32-byte
    /// private key). Used together with <paramref name="subkeyDerivation"/> to produce
    /// the team-scoped keypair per ADR 0032 §Device identity. The closure
    /// captures the reference; derivation runs once per team at
    /// registrar-invocation time.</param>
    /// <param name="sqlCipherKeyDerivation">The installed
    /// <see cref="ISqlCipherKeyDerivation"/> — used at store-activation time
    /// (Wave 6.3.E's <c>ITeamStoreActivator</c>) to derive the 32-byte
    /// SQLCipher key from the root seed + team id. Captured in the closure
    /// so the activator can resolve it alongside
    /// <see cref="Harborline.Api.Foundation.LocalFirst.Encryption.IEncryptedStore"/>
    /// from the per-team provider. The derivation itself is NOT invoked during
    /// registrar wiring — the store is registered unopened; see
    /// <c>_shared/product/wave-6.3-decomposition.md</c> §6.3.B for the
    /// Option-A rationale (deferred <c>OpenAsync</c>).</param>
    /// <returns>A <see cref="TeamServiceRegistrar"/> whose body wires the
    /// per-team ledger trio (6.3.B), sync pair (6.3.C), and bucket registry
    /// (6.3.D).</returns>
    /// <param name="listenForPeers">When <c>true</c> (the default) each team
    /// binds a per-team listening transport endpoint
    /// (<see cref="TeamPaths.TransportEndpoint"/>, a Unix-domain socket on POSIX
    /// / a named pipe on Windows) and the gossip-backed
    /// <see cref="ITeamNotificationStream"/> is wired — the Bridge multi-tenant
    /// posture. When <c>false</c> the team uses an <b>outbound-only</b> transport
    /// (no socket bind) and the null-object <see cref="EmptyTeamNotificationStream"/>:
    /// the embedded single-device single-team node (ADR 0115; Tauri sidecar)
    /// needs no inbound peer sync until multi-device sync lands (Stage 5,
    /// POST-v1), and binding a listening UDS at the platform-conventional
    /// per-user data directory exceeds the macOS 104-char <c>sun_path</c> limit
    /// (the bind throws, taking the host down). The object graph
    /// (<see cref="ISyncDaemonTransport"/>, <see cref="IGossipDaemon"/>,
    /// <see cref="Harborline.Api.Kernel.Lease.ILeaseCoordinator"/>, the ledger) stays
    /// fully resolvable — only the listening socket is suppressed. The durable
    /// length-bounded UDS-path fix is a Stage-5 prerequisite tracked in
    /// <c>kernel-runtime</c> (bug-2847).</param>
    /// <param name="listenBindEndpoint">Cross-machine LISTEN bind endpoint in
    /// <c>tcp://host:port</c> form, consumed ONLY when
    /// <paramref name="listenForPeers"/> is <c>true</c>. When <c>null</c> (the
    /// default) the listening transport binds the safe ephemeral loopback default
    /// (<c>tcp://127.0.0.1:0</c>) — the Bridge multi-tenant posture, where each
    /// tenant child speaks on its own never-colliding loopback port. The embedded
    /// multi-device node passes a LAN-routable <c>0.0.0.0:7473</c> here so a remote
    /// peer can reach the listener; the daemon is trust-gated + DoS-capped, so
    /// binding <c>0.0.0.0</c> is the intended production shape (multi-device INC-5).</param>
    /// <exception cref="ArgumentException"><paramref name="dataDirectory"/>
    /// is <c>null</c> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="subkeyDerivation"/>,
    /// <paramref name="rootIdentity"/>, or <paramref name="sqlCipherKeyDerivation"/>
    /// is <c>null</c>.</exception>
    /// <param name="roundIntervalSeconds">Optional override for the per-team gossip
    /// daemon's periodic anti-entropy round interval
    /// (<see cref="GossipDaemonOptions.RoundIntervalSeconds"/>). When <c>null</c>
    /// (the default) the library default (30s, paper §6.1) is kept — the Bridge
    /// multi-tenant + test posture. The embedded single-user node passes a snappier
    /// value (e.g. 5s) for "edit-here-see-there" UX; with push-on-change wired this
    /// is only the missed-push backstop cadence, not the primary latency knob.</param>
    public static TeamServiceRegistrar Compose(
        string dataDirectory,
        ITeamSubkeyDerivation subkeyDerivation,
        NodeIdentity rootIdentity,
        ISqlCipherKeyDerivation sqlCipherKeyDerivation,
        bool listenForPeers = true,
        string? listenBindEndpoint = null,
        int? roundIntervalSeconds = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(subkeyDerivation);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(sqlCipherKeyDerivation);

        return (services, teamId, outerProvider) =>
        {
            // ── Container-bridge (ONR survey 2026-06-19, CIC option a + a2) ──────────
            //
            // Bridge the install-level synced-doctype delta plane into THIS team's
            // daemon. Registered BEFORE AddHarborlineKernelSync so its TryAdd-Noop
            // IDeltaProducer/IDeltaSink defaults defer (the registration discipline
            // the IDeltaProducer/IDeltaSink contracts document).
            //
            // DEPENDENCY DIRECTION (the one real design nuance): we resolve the
            // INTERFACES IDeltaProducer/IDeltaSink (which live in kernel-sync) from
            // the OUTER provider — NOT the concrete app-level ContactCrdtProjection.
            // kernel-runtime must NOT depend on local-node-host. The outer provider
            // registers the install-level DeltaRoutingRegistry (a2) as both
            // interfaces via AddHarborlineDeltaRouter; the contacts projection is its
            // first entry. So the per-team daemon ships/receives REAL contact deltas
            // through the router, while ADR 0032's per-team daemon stays in the child.
            //
            // LIFETIME: the outer router/projection is a process-lifetime singleton —
            // it outlives every TeamContext, so resolving it from the outer provider
            // in a child factory closure is disposal-safe (the child never owns it).
            //
            // GetService (not GetRequiredService): a composition root that wired no
            // delta plane (an empty outer provider — the Wave 6.1 baseline / DI
            // composition tests) gracefully leaves these unregistered, so
            // AddHarborlineKernelSync's Noop defaults take over and the daemon stays
            // fully resolvable. A production host (local-node-host) always registers
            // the router, so the bridge binds.
            var outerProducer = outerProvider.GetService<IDeltaProducer>();
            var outerSink = outerProvider.GetService<IDeltaSink>();
            if (outerProducer is not null)
            {
                services.AddSingleton<IDeltaProducer>(outerProducer);
            }
            if (outerSink is not null)
            {
                services.AddSingleton<IDeltaSink>(outerSink);
            }

            // Wave 6.3.B — ledger trio. The three services are grouped because
            // IQuarantineQueue's only per-team state is transitive through
            // IEventLog, and both it and IEncryptedStore share the same
            // directory-layout shape. All three flow from TeamPaths.
            services.AddHarborlineEventLog(o =>
            {
                o.Directory = TeamPaths.EventLogDirectory(dataDirectory, teamId);
                o.EpochId = "epoch-0";
            });
            services.AddHarborlineQuarantineQueue();
            var installFootprints = outerProvider.GetService<IInstallFootprintProvider>();
            if (installFootprints is not null)
            {
                services.AddSingleton(installFootprints);
            }
            services.AddHarborlineEncryptedStore(o =>
            {
                o.DatabasePath = TeamPaths.DatabasePath(dataDirectory, teamId);
                o.KeystoreKeyName = TeamPaths.KeystoreKeyName(teamId);
            });

            // Wave 6.3.B stop-work resolution — SQLCipher key provisioning.
            //
            // The registrar does NOT call OpenAsync here (it would need the
            // root seed + an async context, and the registrar is a sync
            // delegate invoked inside TeamContextFactory.CreateAsync). Instead,
            // the 32-byte SQLCipher key is derived on demand by a later hosted
            // service (Wave 6.3.E's ITeamStoreActivator) that resolves
            // ISqlCipherKeyDerivation from the outer provider, reads the root
            // seed from whatever keystore facade the composition root wires,
            // and calls
            //   IEncryptedStore.OpenAsync(
            //       TeamPaths.DatabasePath(dataDirectory, teamId),
            //       sqlCipherKeyDerivation.DeriveSqlCipherKey(rootSeed, teamId.Value.ToString("D")),
            //       ct);
            //
            // sqlCipherKeyDerivation is captured in this closure so that when
            // 6.3.E's activator is dispatched it has a single, stable reference
            // to the exact derivation used at registrar-compose time — no
            // need to double-resolve it from the outer provider.
            _ = sqlCipherKeyDerivation; // reserved for 6.3.E activator wiring.

            // Wave 6.3.C — sync pair. Derives a team-scoped Ed25519 keypair
            // from the root identity + team id via TeamScopedNodeIdentity.Derive
            // (kernel-sync; ADR 0032 §Device identity), registers a per-team
            // INodeIdentityProvider seeded from it, then wires the gossip
            // daemon and lease coordinator against that identity. The team id
            // is rendered in GUID "D" form for the HKDF info string so it
            // matches the derivation contract in TeamSubkeyDerivation
            // (kernel-security) byte-for-byte.
            var teamIdentity = TeamScopedNodeIdentity.Derive(
                rootIdentity, teamId.Value.ToString("D"), subkeyDerivation);

            // Guard per 6.3.C risk note "Root/team identity conflation": the
            // install-level INodeIdentityProvider fallback in
            // AddHarborlineKernelSync registers itself via TryAddSingleton and
            // would win if the outer container had already leaked one in. We
            // defensively clear any inherited registration and install the
            // team-scoped one ahead of AddHarborlineKernelSync.
            services.RemoveAll<INodeIdentityProvider>();
            services.AddSingleton<INodeIdentityProvider>(
                new InMemoryNodeIdentityProvider(teamIdentity));

            // Per-team transport endpoint resolves the decomposition plan's
            // stop-work item #1: each team speaks on its own socket / named
            // pipe, so the install-level daemon does not need HELLO-level
            // team-id multiplexing. AddHarborlineKernelSync's default transport
            // registration is TryAddSingleton, so we RemoveAll first and
            // install the per-team transport for this team.
            //
            // listenForPeers gates the LISTENING (inbound) side only:
            //   * true  (multi-device / Bridge multi-tenant) → bind a TCP
            //     listening endpoint (sync-daemon-protocol §2.1, default port
            //     7473) on an ephemeral loopback port so LAN/loopback peers can
            //     connect inbound. Multi-device INC-5: the listener is TCP, NOT
            //     a Unix-domain socket — binding a listening UDS at the macOS
            //     per-user data dir exceeds the 104-char sun_path limit and
            //     crashes the host (bug-2847). TCP has no path-length limit, so
            //     flipping the listener on no longer trips that bug.
            //   * false (embedded single-device node) → an outbound-only
            //     transport (no socket bind). The single-device node has no
            //     inbound peers until multi-device sync, so nothing binds.
            //     The lease coordinator below still receives the would-be
            //     endpoint string for its localListenEndpoint (advertised
            //     identity), but nothing binds it.
            var transportEndpoint = TeamPaths.TransportEndpoint(dataDirectory, teamId);
            services.RemoveAll<ISyncDaemonTransport>();
            if (listenForPeers)
            {
                // INC-5: TCP listener. The OS-assigned port (for an ephemeral
                // port-0 bind) is surfaced on TcpSyncDaemonTransport.ListenEndpoint
                // for the mDNS advertisement / AddPeer dial target.
                //
                // listenBindEndpoint resolves the cross-machine production shape:
                //   * null (Bridge multi-tenant) → tcp://127.0.0.1:0 — an ephemeral
                //     loopback bind that never collides and never trips sun_path;
                //     each tenant child speaks on its own loopback port and no
                //     remote peer dials in.
                //   * "tcp://0.0.0.0:7473" (embedded multi-device node) → a fixed
                //     LAN-routable bind a remote peer can reach. The daemon is
                //     trust-gated (MemberSetTrustPolicy below) + DoS-capped, so
                //     binding 0.0.0.0 is intended, not an exposure.
                var bindEndpoint = string.IsNullOrWhiteSpace(listenBindEndpoint)
                    ? "tcp://127.0.0.1:0"
                    : listenBindEndpoint;
                services.AddSingleton<ISyncDaemonTransport>(_ =>
                    new TcpSyncDaemonTransport(bindEndpoint));
            }
            else
            {
                services.AddSingleton<ISyncDaemonTransport>(_ =>
                    new UnixSocketSyncDaemonTransport());
            }

            // Enrollment Phase B — the LIVE MULTI-USER trust gate, registered into
            // the per-team child container so the daemon's factory injects it into
            // BOTH the live initiator and the live accept loop. This REPLACES the
            // shared-root gate (the #1288 M1 carry-forward: SharedRootTrustPolicy was
            // the production registration that left #1277-B1 closed only in the unit
            // overload, not end-to-end). The gate now trusts any ENROLLED member's
            // team-scoped TRANSPORT subkey, which is the property the shared-root
            // shortcut could never deliver: two DISTINCT-root users trust each other
            // because both subkeys are in the roster's trusted set (cerebrum
            // [2026-06-20]).
            //
            //   * THIS device's own team subkey (teamIdentity.PublicKey) is ALWAYS in
            //     the trusted set — so a single-user node still trusts its own sibling
            //     devices (same root seed + team id derive the SAME subkey, HKDF/ADR
            //     0032) and an EMPTY roster never bricks a fresh node. This preserves
            //     the exact same-root behavior SharedRootTrustPolicy had as the floor.
            //   * PLUS every admitted member's team-scoped transport subkey, supplied
            //     by the install-level ITrustedMemberKeyProvider resolved from the
            //     OUTER provider (the same dependency-direction trick the container-
            //     bridge above uses — kernel-runtime resolves a kernel-sync INTERFACE
            //     from the outer provider, never depending on local-node-host). The
            //     provider is read on every handshake (the Func snapshot), so an
            //     admit/revoke applies on the next round (subject to the eventual-
            //     convergence offline window).
            //
            // GetService (not GetRequiredService): a composition root that registered
            // no provider (the empty outer provider — DI composition tests, a Bridge
            // multi-tenant child) gets the own-subkey floor alone, fail-closed for any
            // non-derived peer — identical to the prior shared-root posture.
            var memberKeyProvider = outerProvider.GetService<ITrustedMemberKeyProvider>();
            var ownTransportKey = teamIdentity.PublicKey;

            // The trusted-set snapshot func (own-subkey floor ∪ live admitted-member keys). Hoisted out so BOTH the
            // policy AND the optional diagnostic decorator's trusted-set-COUNT accessor read the SAME source.
            IReadOnlyList<byte[]> TrustedKeysSnapshot()
            {
                // Always include this device's own team subkey (the never-brick floor),
                // unioned with the live admitted-member transport keys (if a provider
                // is wired). Read fresh each call so roster changes apply live.
                var keys = new List<byte[]> { ownTransportKey };
                if (memberKeyProvider is not null)
                {
                    keys.AddRange(memberKeyProvider.TrustedTransportKeys());
                }
                return keys;
            }

            IPeerTrustPolicy trustPolicy = PeerTrustPolicyStartupAssertion.Require(
                new MemberSetTrustPolicy(TrustedKeysSnapshot));

            // ADDITIVE DEV DIAGNOSTICS (default-on pre-release) — when the host registered a no-secret peer-trust
            // observer, wrap the policy in a BEHAVIOUR-NEUTRAL diagnostic decorator that surfaces the (unchanged)
            // PEER_UNTRUSTED-vs-trusted decision + key LENGTH + trusted-set COUNT (NEVER a key value) in the host
            // console. The decorator forwards the decision unchanged; absent observer ⇒ no wrap (the trust path is
            // byte-identical to before). The observer is resolved from the OUTER provider via GetService (the same
            // dependency-direction trick the trusted-key provider above uses — kernel-runtime resolves a kernel-sync
            // INTERFACE the host registered, never depending on local-node-host).
            var trustObserver = outerProvider.GetService<IPeerTrustDiagnosticObserver>();
            if (trustObserver is not null)
            {
                trustPolicy = new DiagnosticPeerTrustPolicy(
                    trustPolicy,
                    observe: trustObserver.OnPeerTrustDecision,
                    trustedSetCount: () => TrustedKeysSnapshot().Count);
            }

            services.AddSingleton<IPeerTrustPolicy>(
                PeerTrustPolicyStartupAssertion.Require(trustPolicy));

            services.AddHarborlineDurablePublishedPositions(
                Path.Combine(TeamPaths.TeamRoot(dataDirectory, teamId), "sync"));

            var networkTrust = outerProvider.GetService<INetworkTrustState>();
            if (networkTrust is not null)
            {
                services.AddSingleton(networkTrust);
            }

            // #1301 F-1 — the PRE-TRUST ENROLLMENT handler, registered into the per-team child container so the
            // daemon's factory injects it into the accept loop. Resolved from the OUTER provider via GetService
            // (the SAME dependency-direction trick the trust policy above uses — kernel-runtime resolves a
            // kernel-sync INTERFACE the host registered, never depending on local-node-host). When present, the
            // daemon's network listener offers an invite-gated pre-trust enrollment phase BEFORE the trusted HELLO
            // so a remote not-yet-trusted peer can bootstrap mutual trust over the wire. When ABSENT (the empty
            // outer provider — DI tests, a Bridge multi-tenant child, a sync-only deployment) the listener has NO
            // enrollment surface — an enrollment frame closes fail-closed, identical to the pre-#1301 posture.
            // ONLY wired when the team listens for peers — an outbound-only single-device node has no inbound
            // listener to expose an enrollment phase on.
            if (listenForPeers)
            {
                var enrollmentHandler = outerProvider.GetService<IPreTrustEnrollmentHandler>();
                if (enrollmentHandler is not null)
                {
                    services.AddSingleton<IPreTrustEnrollmentHandler>(enrollmentHandler);
                }
            }

            // AddHarborlineKernelSync fills in VectorClock, IEd25519Signer, and
            // IGossipDaemon against the pre-registered INodeIdentityProvider,
            // ISyncDaemonTransport, and IPeerTrustPolicy. Its TryAddSingleton
            // guards mean our earlier registrations are honored, and its daemon
            // factory resolves the trust policy we just registered.
            //
            // roundIntervalSeconds (snappy-convergence follow-up): when the
            // composition root passed an override (the embedded single-user node
            // wants a snappier anti-entropy cadence than the paper 30s), configure
            // the per-team GossipDaemonOptions accordingly. When null (Bridge
            // multi-tenant + tests) the library default is kept — no Configure call,
            // so AddOptions' 30s default stands.
            if (roundIntervalSeconds is { } interval)
            {
                services.AddHarborlineKernelSync(o => o.RoundIntervalSeconds = Math.Max(1, interval));
            }
            else
            {
                services.AddHarborlineKernelSync();
            }

            // Sync-status read-model (Phase A — sync-status-read-model survey
            // 2026-06-19). A per-team projector over THIS team's daemon: it
            // subscribes to the daemon's FrameReceived + RoundCompleted events in
            // its constructor and snapshots KnownPeers on demand, turning the
            // daemon's transient signals + per-peer bookkeeping into the four-state
            // sync-status surface the Harborline App UI reads (GET /api/local-node/sync-
            // status). Registered as a per-team singleton alongside the daemon; the
            // host resolves it from the active team's provider, which constructs
            // it (and thus wires its subscription) at endpoint start. Zero
            // wire-protocol change — a pure read projection.
            services.AddSingleton<ISyncStatusReadModel>(sp =>
                new SyncStatusReadModel(
                    sp.GetRequiredService<IGossipDaemon>(),
                    sp.GetRequiredService<IOptions<GossipDaemonOptions>>(),
                    sp.GetRequiredService<TimeProvider>()));

            // Lease coordinator's localNodeId is deterministic from the team
            // subkey's public key (first 16 bytes, lowercase hex). Identically
            // configured installs on different machines produce the same
            // localNodeId for the same root seed + team id — the test suite
            // pins that round-trip.
            var localNodeId = Convert.ToHexString(
                teamIdentity.PublicKey.AsSpan(0, 16)).ToLowerInvariant();
            services.AddHarborlineKernelLease(
                localNodeId: localNodeId,
                localListenEndpoint: transportEndpoint);

            // ADR 0115 Stage 1 — compose the kernel ledger into the per-team
            // service provider. PostingEngine needs ILeaseCoordinator (registered
            // above) and IEventLog (registered in Wave 6.3.B). With this call
            // the posting engine, balance + statement projections, and period
            // closer are all available per-team — resolving council Finding A1.
            services.AddHarborlineKernelLedger();

            // Wave 6.3.D — buckets per-team. Manifest source directory is
            // TeamPaths.BucketsDirectory(dataDirectory, teamId). IBucketRegistry,
            // IBucketYamlLoader, IBucketFilterEvaluator, IBucketStubStore, and
            // IStorageBudgetManager are all installed per-team (TryAddSingleton
            // against this team's fresh ServiceCollection yields per-team
            // singletons; each TeamContext's provider owns its own instances).
            var bucketsDirectory = TeamPaths.BucketsDirectory(dataDirectory, teamId);
            services.AddHarborlineKernelBuckets(o => o.SourceDirectory = bucketsDirectory);

            // Wave 6.5 — real notification producer. Subscribes to this
            // team's IGossipDaemon (registered above by AddHarborlineKernelSync)
            // and emits TeamNotifications into the INotificationAggregator.
            // Without this the aggregator fan-in would still see only the
            // EmptyTeamNotificationStream placeholder and badge counts would
            // be pinned at zero even as inter-peer traffic flowed.
            //
            // When listenForPeers is false (embedded single-device node) there
            // is no inter-peer traffic to surface, so we register the
            // null-object EmptyTeamNotificationStream — badge counts idle at
            // zero, which is correct for a node with no sync peers.
            if (listenForPeers)
            {
                services.AddSingleton<ITeamNotificationStream>(sp =>
                    new GossipEventTeamNotificationStream(
                        teamId, sp.GetRequiredService<IGossipDaemon>()));
            }
            else
            {
                services.AddSingleton<ITeamNotificationStream>(
                    new EmptyTeamNotificationStream(teamId));
            }
        };
    }
}
