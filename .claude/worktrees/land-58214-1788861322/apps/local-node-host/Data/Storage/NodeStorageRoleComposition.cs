using System;
using System.IO;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Blobs;
using Harborline.Api.Kernel.Buckets.Storage.Durability;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;

namespace Harborline.Api.LocalNodeHost.Data.Storage;

/// <summary>
/// PERS-1 / PERS-2 — the STORAGE-ROLE blob composition, extracted from <c>Program.cs</c> so it is a SINGLE, REUSABLE
/// code path exercised BOTH by the host at boot AND by the arch-fence over the real host graph (PERS-2 F4). Before
/// the extraction the arch-fence built a hand-rolled <c>ServiceCollection</c> that MIMICKED <c>Program.cs</c>; a
/// drift in the real composition (e.g. dropping the posture gate, or letting the edge default win) would not have
/// been caught. Now the fence runs THIS method — the same one the host runs.
/// </summary>
public static class NodeStorageRoleComposition
{
    /// <summary>
    /// Wire the durable, mandatory-envelope-at-rest storage-role blob store (ADR 0137 D4 / C-3; ADR 0127 seam): the
    /// fail-closed key-posture gate, then a <c>FileSystemBlobStore</c> rooted at <c>{dataDirectory}/blobs</c> wrapped
    /// by an <c>EnvelopeBlobStore</c>, then wrapped again by a <c>DurabilityGuardedBlobStore</c> so
    /// <see cref="Harborline.Api.Foundation.Blobs.IBlobStore.UnpinAsync"/> is durability-gated (PERS-2 F-Min-3 — symmetry
    /// with the record-eviction seam). The raw backend is never registered directly (the no-side-door invariant).
    /// </summary>
    /// <param name="services">The composition-root service collection (the real host's, or the fence's).</param>
    /// <param name="dataDirectory">The node data directory; the blob store is rooted at <c>{dataDirectory}/blobs</c>.</param>
    /// <param name="genesisTeamId">The genesis team's projected tenant, under which the blob DEK is derived.</param>
    public static IServiceCollection ConfigureStorageRoleBlobStore(
        this IServiceCollection services, string dataDirectory, string genesisTeamId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(genesisTeamId);

        // Fail-closed key posture (PERS-2 F1 allowlist): the effective ITenantKeyProvider MUST be the real
        // install-secret RootSeedTenantKeyProvider, never a derivable stand-in.
        services.RequireRealBlobEnvelopeKeyProvider();

        var blobRoot = Path.Combine(dataDirectory, "blobs");
        var blobTenant = new TenantId(genesisTeamId);
        services.AddStorageRoleBlobStore(blobRoot, blobTenant);

        // PERS-2 F-Min-3: register the durability guard substrate (fail-closed defaults) and wrap the envelope store
        // so the blob-retention shed seam (UnpinAsync) is durability-gated, symmetric with the record-eviction seam.
        // A canonical storage node's blobs are thus refused fail-closed on unpin until N independent copies are
        // confirmed elsewhere.
        services.AddHarborlineDurabilityGuard();
        services.AddDurabilityGuardedBlobStore();
        return services;
    }

    /// <summary>
    /// Wire the durable, mandatory-envelope-at-rest EDGE-role blob store (Harborline card 3750 / gap G2.1): the SAME
    /// fail-closed key-posture gate plus <c>FileSystemBlobStore</c>-inside-<c>EnvelopeBlobStore</c> wiring as the
    /// storage role, WITHOUT the storage-role durability guard. The guard's <c>UnpinAsync</c> gating describes a
    /// CANONICAL multi-copy store's shed seam; no shed caller exists today, and when one lands, the edge role's
    /// canonical-copy semantics need deciding then. Ciphertext-at-rest and the no-side-door invariant are identical
    /// to the storage role: the raw backend is never the registered <see cref="Harborline.Api.Foundation.Blobs.IBlobStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this method existed, the edge role fell through to <c>AddNodeForms</c>'
    /// <c>TryAddSingleton&lt;IBlobStore, NodeInMemoryBlobStore&gt;</c> — a VOLATILE store on the Harborline App's bundled
    /// node (no shipped config sets a role, so every Harborline App install ran it): blobs died with the process and were
    /// never sealed at rest. Register this BEFORE <c>AddNodeForms</c> so the durable envelope store wins the
    /// <c>TryAdd</c>; the in-memory store remains reachable only by explicit registration (tests).
    /// </para>
    /// <para>
    /// <b>CID semantics change under the envelope.</b> The store-returned CID addresses the SEALED envelope (random
    /// nonce ⇒ identical plaintext yields different CIDs; C-3 no-equality-oracle). A caller that computes a
    /// plaintext CID out-of-band (e.g. a <c>Schema.ContentAddress</c>-style content hash) can no longer
    /// <c>GetAsync</c> by that plaintext CID — only CIDs returned by <c>PutAsync</c> resolve.
    /// </para>
    /// <para>
    /// <b>Key scoping.</b> All blobs seal under ONE DEK derived for <paramref name="genesisTeamId"/>'s projected
    /// tenant. On a multi-team edge node every team's blobs share that genesis-team DEK — this wiring provides
    /// ciphertext-at-rest, NOT per-team key separation, and cannot support a per-team blob crypto-shred.
    /// </para>
    /// </remarks>
    /// <param name="services">The composition-root service collection (the real host's, or a test's).</param>
    /// <param name="dataDirectory">The node data directory; the blob store is rooted at <c>{dataDirectory}/blobs</c>.</param>
    /// <param name="genesisTeamId">The genesis team's projected tenant, under which the blob DEK is derived.</param>
    public static IServiceCollection ConfigureEdgeDurableBlobStore(
        this IServiceCollection services, string dataDirectory, string genesisTeamId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(genesisTeamId);

        // Same fail-closed key posture as the storage role (PERS-2 F1 allowlist): the effective
        // ITenantKeyProvider MUST be the real install-secret RootSeedTenantKeyProvider.
        services.RequireRealBlobEnvelopeKeyProvider();

        var blobRoot = Path.Combine(dataDirectory, "blobs");
        var blobTenant = new TenantId(genesisTeamId);
        services.AddStorageRoleBlobStore(blobRoot, blobTenant);
        return services;
    }

    /// <summary>
    /// Fail-closed composition-root gate (card 3750 deep-review finding 2; the <c>RequireDurableErasureStores</c>
    /// idiom from the 1378 M-1 class): throw unless an <see cref="Harborline.Api.Foundation.Blobs.IBlobStore"/> is
    /// registered and its effective (last) descriptor is durable wiring. Deleting either role branch's blob wiring
    /// in <c>Program.cs</c> would otherwise silently revert the node to the volatile in-memory default with every
    /// suite green — this gate turns that regression into a hard startup failure. Call it from the composition root
    /// AFTER both role branches and after <c>AddNodeForms</c>.
    /// </summary>
    /// <remarks>
    /// Like the erasure-store gate, the check is conservative and descriptor-level: it rejects a MISSING
    /// registration, the known volatile default (<c>NodeInMemoryBlobStore</c>), and a raw
    /// <see cref="Harborline.Api.Foundation.Blobs.FileSystemBlobStore"/> type registration (the C-3 side-door). The
    /// durable envelope wiring registers via factory (opaque at composition time), which — exactly as in the
    /// precedent — is accepted as the host's deliberate durable choice; the envelope-chain SHAPE itself is proved
    /// by the arch-fence and the 3750 gates over the real composition methods.
    /// </remarks>
    /// <param name="services">The composition-root service collection to validate.</param>
    /// <exception cref="InvalidOperationException">
    /// No <c>IBlobStore</c> is registered, or the effective registration is the restart-volatile in-memory default
    /// or the raw unsealed filesystem backend.
    /// </exception>
    public static IServiceCollection RequireDurableBlobStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The effective registration for a service type is the LAST descriptor added for it (DI semantics).
        Microsoft.Extensions.DependencyInjection.ServiceDescriptor? effective = null;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(Harborline.Api.Foundation.Blobs.IBlobStore))
            {
                effective = services[i];
                break;
            }
        }

        if (effective is null)
        {
            throw new InvalidOperationException(
                "Durable blob-store gate (card 3750): no IBlobStore is registered. Every node role must wire the "
                + "durable envelope-sealed blob store (ConfigureStorageRoleBlobStore / ConfigureEdgeDurableBlobStore) "
                + "before RequireDurableBlobStore() runs.");
        }

        var concreteType = effective.ImplementationInstance?.GetType() ?? effective.ImplementationType;

        if (concreteType is not null && concreteType.Name == "NodeInMemoryBlobStore")
        {
            throw new InvalidOperationException(
                "Durable blob-store gate (card 3750): IBlobStore still resolves to the restart-volatile "
                + "NodeInMemoryBlobStore default — blobs would die with the process and never seal at rest (the "
                + "G2.1 data-loss defect). Wire the durable envelope-sealed store for this role before this gate.");
        }

        if (concreteType == typeof(Harborline.Api.Foundation.Blobs.FileSystemBlobStore))
        {
            throw new InvalidOperationException(
                "Durable blob-store gate (card 3750): IBlobStore resolves to the RAW FileSystemBlobStore — plaintext "
                + "at rest, violating the C-3 no-side-door invariant. The filesystem backend may exist only inside "
                + "the EnvelopeBlobStore wrap.");
        }

        return services;
    }
}
