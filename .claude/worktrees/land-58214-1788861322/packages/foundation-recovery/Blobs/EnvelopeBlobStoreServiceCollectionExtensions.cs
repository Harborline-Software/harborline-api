using System;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery.TenantKey;

namespace Harborline.Api.Foundation.Recovery.Blobs;

/// <summary>
/// PERS-1 DI registration for the mandatory-envelope-at-rest blob store (ADR 0137 D4 / ADR 0127). Registers an
/// <see cref="EnvelopeBlobStore"/> as the public <see cref="IBlobStore"/>, wrapping a raw backend so the storage
/// role never persists plaintext at rest. There is no blob <c>ServiceCollectionExtensions</c> in the foundation
/// project (and there cannot be one that references <see cref="ITenantKeyProvider"/> without a circular project
/// reference), so the storage-role blob wiring lives here, in foundation-recovery.
/// </summary>
public static class EnvelopeBlobStoreServiceCollectionExtensions
{
    /// <summary>
    /// Register an <see cref="EnvelopeBlobStore"/> as the singleton <see cref="IBlobStore"/>, wrapping the raw inner
    /// store produced by <paramref name="innerFactory"/>. The <see cref="ITenantKeyProvider"/> is resolved from the
    /// container at first use (so it picks up the host's real, install-secret provider).
    /// </summary>
    /// <remarks>
    /// Registered as a plain <c>AddSingleton</c> (last-wins) so it deposes a prior
    /// <c>TryAddSingleton&lt;IBlobStore, …&gt;</c> default when registered after it, and is itself suppressed by
    /// nothing it follows. The storage-role composition registers this BEFORE the in-memory edge default's
    /// <c>TryAdd</c>, so the envelope store wins deterministically.
    /// </remarks>
    public static IServiceCollection AddEnvelopeBlobStore(
        this IServiceCollection services, Func<IServiceProvider, IBlobStore> innerFactory, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(innerFactory);

        services.AddSingleton<IBlobStore>(sp => new EnvelopeBlobStore(
            innerFactory(sp),
            sp.GetRequiredService<ITenantKeyProvider>(),
            tenant));

        return services;
    }

    /// <summary>
    /// Register the storage-role blob store: a durable filesystem backend (<see cref="FileSystemBlobStore"/> rooted
    /// at <paramref name="blobRootDirectory"/>) wrapped by an <see cref="EnvelopeBlobStore"/>, registered as the
    /// public <see cref="IBlobStore"/>. The raw <see cref="FileSystemBlobStore"/> is NEVER registered as
    /// <see cref="IBlobStore"/> directly — it exists only inside the envelope wrap (the C-3 no-side-door invariant).
    /// </summary>
    /// <param name="services">The composition-root service collection.</param>
    /// <param name="blobRootDirectory">The directory the durable ciphertext envelopes are stored under.</param>
    /// <param name="tenant">The ONE tenant the blob DEK is derived under. On a multi-team node all teams' blobs
    /// seal under this single tenant's DEK (ciphertext-at-rest, not per-team key separation or per-team shred).</param>
    public static IServiceCollection AddStorageRoleBlobStore(
        this IServiceCollection services, string blobRootDirectory, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRootDirectory);

        // The raw FileSystemBlobStore is constructed ONLY here, inside the envelope-wrapping factory — it is never a
        // standalone IBlobStore registration. This is the single legitimate `new FileSystemBlobStore(...)` in the
        // storage-role wiring path, and it is always wrapped.
        return services.AddEnvelopeBlobStore(_ => new FileSystemBlobStore(blobRootDirectory), tenant);
    }
}
