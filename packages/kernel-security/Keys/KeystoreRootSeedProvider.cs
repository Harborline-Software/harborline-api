using System.Security.Cryptography;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.LocalFirst.Installation;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// <see cref="IRootSeedProvider"/> backed by an install-namespaced <see cref="IKeystore"/> slot.
/// First-call-of-first-launch generates a 32-byte RNG seed and persists it;
/// subsequent calls return the cached in-process value without re-hitting the
/// keystore.
/// </summary>
/// <remarks>
/// <para>
/// Slot prefix <c>"sunfish:root-seed:v1:"</c> is followed by the durable install identity and is
/// namespaced separately from the
/// per-team subkey slots (<c>"sunfish:team:{teamId}:primary"</c>) and from the
/// pre-existing <c>"sunfish-primary"</c> slot used by older Wave-2 code paths,
/// so no consumer can collide with this provider's storage.
/// </para>
/// <para>
/// Concurrency: the first call materializes a <see cref="Lazy{T}"/>
/// <see cref="Task{TResult}"/> under
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>. Concurrent first
/// callers therefore share a single <c>GetKeyAsync</c> / <c>SetKeyAsync</c>
/// round-trip; the RNG draw happens exactly once per process-lifetime of this
/// provider.
/// </para>
/// <para>
/// Failure modes: if <see cref="IKeystore.SetKeyAsync"/> throws (for example
/// <see cref="PlatformNotSupportedException"/> from the Wave-2 macOS / Linux
/// stubs in <see cref="Keystore.CreateForCurrentPlatform(string?)"/>), the
/// exception propagates. Per-install isolation requires the keystore to work,
/// so silently falling back to an on-disk or in-memory seed would be a
/// correctness regression. The Windows DPAPI happy path is the only one that
/// must succeed today; mac/Linux support lands with the Wave-2 keystore work.
/// </para>
/// </remarks>
public sealed class KeystoreRootSeedProvider : IRootSeedProvider, IRootSeedRestorer
{
    /// <summary>Legacy un-namespaced root-seed slot retained for migration.</summary>
    public const string LegacySlotName = "sunfish:root-seed:v1";

    /// <summary>Prefix for install-namespaced root-seed slots.</summary>
    public const string SlotPrefix = LegacySlotName + ":";

    /// <summary>Length of an Ed25519 root seed, in bytes.</summary>
    public const int SeedLength = 32;

    private readonly IKeystore _keystore;
    private readonly IInstallIdentityProvider _installIdentityProvider;
    private readonly object _gate = new();
    private Lazy<Task<ReadOnlyMemory<byte>>>? _cached;

    /// <summary>
    /// Construct a provider bound to the supplied keystore.
    /// </summary>
    /// <param name="keystore">Platform keystore. Callers typically inject the
    /// one produced by <see cref="Keystore.CreateForCurrentPlatform(string?)"/>;
    /// tests pass <see cref="InMemoryKeystore"/>.</param>
    /// <param name="installIdentityProvider">Durable identity shared by all namespaces for this install.</param>
    public KeystoreRootSeedProvider(
        IKeystore keystore,
        IInstallIdentityProvider installIdentityProvider)
    {
        _keystore = keystore ?? throw new ArgumentNullException(nameof(keystore));
        _installIdentityProvider = installIdentityProvider
            ?? throw new ArgumentNullException(nameof(installIdentityProvider));
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> GetRootSeedAsync(CancellationToken ct)
    {
        var lazy = _cached;
        if (lazy is null)
        {
            lock (_gate)
            {
                lazy = _cached ??= new Lazy<Task<ReadOnlyMemory<byte>>>(
                    () => ResolveAsync(ct),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        return new ValueTask<ReadOnlyMemory<byte>>(lazy.Value);
    }

    private async Task<ReadOnlyMemory<byte>> ResolveAsync(CancellationToken ct)
    {
        var slotName = await GetSlotNameAsync(ct).ConfigureAwait(false);
        var existing = await _keystore.GetKeyAsync(slotName, ct).ConfigureAwait(false);
        if (existing is { } buf && buf.Length == SeedLength)
        {
            // Happy path after first launch: the install's seed was already
            // provisioned; return it verbatim.
            return buf;
        }

        if (_keystore is IAtomicKeystore atomicKeystore)
        {
            var legacyCandidate = await _keystore.GetKeyAsync(LegacySlotName, ct).ConfigureAwait(false);
            if (legacyCandidate is { } validLegacyCandidate
                && validLegacyCandidate.Length == SeedLength
                && await atomicKeystore.TryMoveKeyAsync(LegacySlotName, slotName, ct).ConfigureAwait(false))
            {
                var adopted = await _keystore.GetKeyAsync(slotName, ct).ConfigureAwait(false);
                if (adopted is { } adoptedSeed && adoptedSeed.Length == SeedLength)
                {
                    return adoptedSeed;
                }

                throw new InvalidDataException("The atomically adopted legacy root seed is invalid.");
            }

            var concurrentlyProvisioned = await _keystore.GetKeyAsync(slotName, ct).ConfigureAwait(false);
            if (concurrentlyProvisioned is { } concurrentSeed && concurrentSeed.Length == SeedLength)
            {
                return concurrentSeed;
            }
        }
        else
        {
            var legacy = await _keystore.GetKeyAsync(LegacySlotName, ct).ConfigureAwait(false);
            if (legacy is { } legacySeed && legacySeed.Length == SeedLength)
            {
                await _keystore.SetKeyAsync(slotName, legacySeed, ct).ConfigureAwait(false);
                await _keystore.DeleteKeyAsync(LegacySlotName, ct).ConfigureAwait(false);
                return legacySeed;
            }
        }

        // Either no slot yet (fresh install) or a slot with the wrong length
        // (corrupt / partially-written blob from an aborted provisioning).
        // Regenerate via RNG and overwrite. We do not attempt to recover the
        // prior bytes — a wrong-length slot is meaningless as an Ed25519 seed.
        var seed = RandomNumberGenerator.GetBytes(SeedLength);
        await _keystore.SetKeyAsync(slotName, seed, ct).ConfigureAwait(false);
        return seed;
    }

    /// <inheritdoc />
    public async Task RestoreRootSeedAsync(ReadOnlyMemory<byte> recoveredSeed, CancellationToken ct)
    {
        if (recoveredSeed.Length != SeedLength)
        {
            throw new ArgumentException(
                $"Recovered seed must be {SeedLength} bytes (was {recoveredSeed.Length}).",
                nameof(recoveredSeed));
        }

        // Defensive copy: the caller may reuse / clear the source buffer.
        var seed = recoveredSeed.ToArray();
        var slotName = await GetSlotNameAsync(ct).ConfigureAwait(false);
        await _keystore.SetKeyAsync(slotName, seed, ct).ConfigureAwait(false);

        // Invalidate the in-process Lazy<> cache so the next GetRootSeedAsync
        // re-reads the keystore and observes the restored bytes. A subsequent
        // call to GetRootSeedAsync will re-materialize the Lazy under the gate.
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<string> GetSlotNameAsync(CancellationToken ct)
    {
        var identity = await _installIdentityProvider.GetInstallIdentityAsync(ct).ConfigureAwait(false);
        return SlotPrefix + identity.Value;
    }
}
