using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Discovery;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Network;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.Kernel.Sync.Restore;

namespace Harborline.Api.Kernel.Sync.DependencyInjection;

/// <summary>
/// DI extensions for registering the Harborline intra-team gossip daemon
/// (paper §6.1–6.2; ADR 0029; sync-daemon-protocol spec).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the holder-side published-position evidence and the outbound allocator over one durable store.
    /// </summary>
    public static IServiceCollection AddHarborlineDurablePublishedPositions(
        this IServiceCollection services,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        services.TryAddSingleton<IPublishedPositionStore>(
            _ => new FilePublishedPositionStore(directory));
        services.TryAddSingleton<IOutboundSequenceAllocator>(
            sp => new PublishedPositionSequenceAllocator(
                sp.GetRequiredService<IPublishedPositionStore>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="ISyncDaemonTransport"/>, <see cref="IGossipDaemon"/>,
    /// <see cref="VectorClock"/>, <see cref="IEd25519Signer"/>, and
    /// <see cref="INodeIdentityProvider"/> as singletons. Uses
    /// <c>TryAddSingleton</c> so a preceding registration (an
    /// <see cref="InMemorySyncDaemonTransport"/> for tests, a custom
    /// Unix-socket path, a keystore-backed identity provider, etc.) wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default transport is <see cref="UnixSocketSyncDaemonTransport"/>
    /// with no listen endpoint (outbound-only). Applications that need an
    /// inbound listener — the typical daemon deployment — must register
    /// <see cref="ISyncDaemonTransport"/> themselves before calling this.
    /// The default <see cref="VectorClock"/> is an empty instance; the gossip
    /// daemon mutates it in place.
    /// </para>
    /// <para>
    /// <b>Identity fallback.</b> If no <see cref="INodeIdentityProvider"/> is
    /// already registered, one is generated on first resolve by calling
    /// <see cref="IEd25519Signer.GenerateKeyPair"/> and deriving a hex
    /// <c>NodeId</c> from the first 16 bytes of the public key. This is
    /// suitable for tests, bootstrap, and single-node CLI harnesses only.
    /// Production composition roots (for example <c>apps/local-node-host</c>)
    /// register their own <see cref="INodeIdentityProvider"/> backed by an
    /// <c>IKeystore</c> lookup so the keypair survives restarts.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineKernelSync(
        this IServiceCollection services,
        Action<GossipDaemonOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<GossipDaemonOptions>();
        }

        services.TryAddSingleton<ISyncDaemonTransport>(_ => new UnixSocketSyncDaemonTransport());
        services.TryAddSingleton<VectorClock>(_ => new VectorClock());
        services.TryAddSingleton<IEd25519Signer, Ed25519Signer>();

        // Fallback node-identity provider: generates a fresh keypair the
        // first time someone asks. Production composition roots register
        // their own IKeystore-backed INodeIdentityProvider before calling
        // AddHarborlineKernelSync, so this factory only runs in tests and the
        // scaffolding-CLI bootstrap path.
        services.TryAddSingleton<INodeIdentityProvider>(sp =>
        {
            var signer = sp.GetRequiredService<IEd25519Signer>();
            var (publicKey, privateKey) = signer.GenerateKeyPair();
            // Derive a hex NodeId from the first 16 bytes of the public key.
            // Real deployments persist this — the per-process generation here
            // would give each restart a new identity, which is the correct
            // behaviour for the test/bootstrap fallback.
            var nodeIdBytes = new byte[16];
            Buffer.BlockCopy(publicKey, 0, nodeIdBytes, 0, 16);
            var nodeIdHex = Convert.ToHexString(nodeIdBytes).ToLowerInvariant();
            return new InMemoryNodeIdentityProvider(
                new NodeIdentity(nodeIdHex, publicKey, privateKey));
        });

        // Wave 2.5 — DELTA_STREAM application defaults. The gossip daemon's
        // round loop dispatches outbound delta encoding to IDeltaProducer and
        // inbound delta application to IDeltaSink. Defaults are no-ops so
        // PING-only deployments behave unchanged; Anchor / local-node-host
        // register concrete implementations (backed by ICrdtDocument) before
        // calling AddHarborlineKernelSync.
        services.TryAddSingleton<IDeltaProducer, NoopDeltaProducer>();
        services.TryAddSingleton<IDeltaSink, NoopDeltaSink>();

        // Multi-device INC-5 — the daemon is constructed via an explicit factory
        // so an OPTIONAL IPeerTrustPolicy registered by the composition root
        // (a non-null SharedRootTrustPolicy in the per-team child container)
        // flows into BOTH the live initiator and the live accept loop. Resolved
        // via GetService (not GetRequiredService) so absence preserves the
        // pre-INC-5 allow-all behavior for deployments with an out-of-band trust
        // boundary. The factory mirrors the DI-activated ctor for the other
        // dependencies exactly.
        // #1301 F-1 — the OPTIONAL pre-trust enrollment handler flows in the SAME
        // way as IPeerTrustPolicy: resolved via GetService so absence preserves
        // the pre-#1301 behavior (no pre-trust enrollment surface on the network
        // listener — an enrollment frame closes the connection). A composition
        // root (local-node-host) that wants cross-machine enrollment registers an
        // IPreTrustEnrollmentHandler before / alongside this call (the registrar
        // resolves it from the outer provider, like ITrustedMemberKeyProvider).
        services.TryAddSingleton<IGossipDaemon>(sp => new GossipDaemon(
            sp.GetRequiredService<ISyncDaemonTransport>(),
            sp.GetRequiredService<VectorClock>(),
            sp.GetRequiredService<IOptions<GossipDaemonOptions>>(),
            sp.GetRequiredService<INodeIdentityProvider>(),
            sp.GetRequiredService<IEd25519Signer>(),
            sp.GetService<IDeltaProducer>(),
            sp.GetService<IDeltaSink>(),
            sp.GetService<IPeerTrustPolicy>(),
            sp.GetService<ILogger<GossipDaemon>>(),
            sp.GetService<IPreTrustEnrollmentHandler>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            networkTrust: sp.GetService<INetworkTrustState>(),
            outboundSequenceAllocator: sp.GetService<IOutboundSequenceAllocator>()));

        return services;
    }

    /// <summary>
    /// Register the container-bridge id-routed delta seam (a2): a singleton
    /// <see cref="DeltaRoutingRegistry"/> exposed as <see cref="IDeltaRouter"/>
    /// AND as the <see cref="IDeltaProducer"/>/<see cref="IDeltaSink"/> the gossip
    /// daemon resolves. The composition root resolves the <see cref="IDeltaRouter"/>
    /// once and
    /// <see cref="IDeltaRouter.Register(string,IDeltaProducer,IDeltaSink)">registers</see> each synced
    /// doctype (v1: contacts); the per-team daemon then bridges to this router
    /// from the outer provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered in the <b>outer</b> (install-level) provider — it owns the
    /// install-level synced-doctype projections. Uses <c>TryAdd</c> so a test
    /// double or an alternative router wins. Because the router doubles as the
    /// <see cref="IDeltaProducer"/>/<see cref="IDeltaSink"/>, calling this BEFORE
    /// any <c>AddHarborlineKernelSync</c> in the same container makes the router the
    /// container's delta plane (its <c>TryAdd</c>-Noop defaults then defer); a
    /// router with no registrations is itself a Noop, so this is safe even before
    /// a doctype registers.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineDeltaRouter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDeltaRouter, DeltaRoutingRegistry>();
        services.TryAddSingleton<IDeltaProducer>(sp => sp.GetRequiredService<IDeltaRouter>());
        services.TryAddSingleton<IDeltaSink>(sp => sp.GetRequiredService<IDeltaRouter>());
        return services;
    }

    /// <summary>
    /// Register <see cref="MdnsPeerDiscovery"/> as the <see cref="IPeerDiscovery"/>
    /// implementation (paper §6.1 tier-1, LAN-only). Call after
    /// <see cref="AddHarborlineKernelSync"/> — the gossip daemon is resolved
    /// lazily, so registration order only matters for options binding.
    /// </summary>
    /// <remarks>
    /// This registration does not wire the discovery source into the gossip
    /// daemon automatically — the caller must call
    /// <see cref="GossipDaemonDiscoveryExtensions.AttachDiscovery"/> in their
    /// startup path. The bridge is explicit because the lifecycle (when to
    /// start advertising, what <see cref="PeerAdvertisement"/> to publish) is
    /// application-owned.
    /// </remarks>
    public static IServiceCollection AddMdnsPeerDiscovery(
        this IServiceCollection services,
        Action<PeerDiscoveryOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<PeerDiscoveryOptions>();
        }

        services.TryAddSingleton<IPeerDiscovery>(sp => new NetworkTrustPeerDiscovery(
            new MdnsPeerDiscovery(
                sp.GetRequiredService<IOptions<PeerDiscoveryOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<MdnsPeerDiscovery>>()),
            sp.GetService<INetworkTrustState>()
                ?? new ConfiguredNetworkTrustState(NetworkTrustLevel.Known)));
        return services;
    }

    /// <summary>
    /// Register <see cref="InMemoryPeerDiscovery"/> as the
    /// <see cref="IPeerDiscovery"/> implementation. Tests and integration
    /// harnesses only — the shared broker is process-wide.
    /// </summary>
    public static IServiceCollection AddInMemoryPeerDiscovery(
        this IServiceCollection services,
        Action<PeerDiscoveryOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<PeerDiscoveryOptions>();
        }

        services.TryAddSingleton<InMemoryPeerDiscoveryBroker>(_ => InMemoryPeerDiscoveryBroker.Shared);
        services.TryAddSingleton<IPeerDiscovery>(sp =>
            new InMemoryPeerDiscovery(
                sp.GetRequiredService<InMemoryPeerDiscoveryBroker>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PeerDiscoveryOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Register <see cref="ManagedRelayPeerDiscovery"/> as the paper §17.2
    /// tier-3 WAN-relay discovery source. Coexists with
    /// <see cref="AddMdnsPeerDiscovery"/>: this method registers the
    /// concrete <see cref="ManagedRelayPeerDiscovery"/> type, leaving the
    /// <see cref="IPeerDiscovery"/> binding (mDNS / in-memory) untouched.
    /// Composition roots resolve both and call
    /// <see cref="GossipDaemonDiscoveryExtensions.AttachDiscovery"/> twice
    /// — once per source — so the gossip daemon's combined peer set
    /// contains LAN peers from mDNS plus the configured Bridge relay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The concrete-type registration (rather than as <see cref="IPeerDiscovery"/>)
    /// is intentional. <c>TryAddSingleton&lt;IPeerDiscovery&gt;</c> would
    /// silently lose to a sibling registration, hiding a misconfiguration.
    /// Resolving the concrete type makes the multi-source intent explicit
    /// at the call site.
    /// </para>
    /// <para>
    /// <see cref="ManagedRelayPeerDiscoveryOptions.RelayUrl"/> empty is a
    /// supported configuration: <see cref="ManagedRelayPeerDiscovery.StartAsync"/>
    /// becomes a no-op and the discovery surface produces no peers. This
    /// is the LAN-only deployment shape — register the source so the
    /// composition is uniform across deployments, then drive behavior with
    /// the options.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddManagedRelayPeerDiscovery(
        this IServiceCollection services,
        Action<ManagedRelayPeerDiscoveryOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<ManagedRelayPeerDiscoveryOptions>();
        }

        services.TryAddSingleton<ManagedRelayPeerDiscovery>(sp =>
            new ManagedRelayPeerDiscovery(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ManagedRelayPeerDiscoveryOptions>>().Value));
        return services;
    }
}
