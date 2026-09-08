using System.Security.Cryptography;
using System.Text;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>
/// Provides random, stored tenant DEKs and random, stored per-subject DEKs under a hierarchy root.
/// </summary>
/// <remarks>
/// Tenant DEKs are wrapped by the resolved at-rest hierarchy root. Subject DEKs are wrapped by their
/// tenant DEK. Neither key is derived from the hierarchy root or from a subject identifier.
/// </remarks>
public sealed class StoredTenantKeyProvider :
    ITenantKeyProvider,
    IVersionedTenantKeyProvider,
    ITenantKeyDestroyer
{
    private const int LegacyKeyVersion = 1;
    private const int CurrentKeyVersion = 2;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly IStoredTenantKeyStore _store;
    private readonly byte[] _hierarchyRoot;
    private readonly ITenantKeyProvider? _legacyProvider;

    /// <summary>Construct a stored provider under the resolved 32-byte at-rest hierarchy root.</summary>
    public StoredTenantKeyProvider(
        IStoredTenantKeyStore store,
        ReadOnlySpan<byte> hierarchyRoot,
        ITenantKeyProvider? legacyProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (hierarchyRoot.Length != KeyLength)
        {
            throw new ArgumentException(
                $"Hierarchy root must be exactly {KeyLength} bytes.",
                nameof(hierarchyRoot));
        }

        _hierarchyRoot = hierarchyRoot.ToArray();
        _legacyProvider = legacyProvider;
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(
        TenantId tenant,
        string purpose,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var slot = BuildTenantSlot(tenant, purpose, CurrentKeyVersion);
        return GetOrCreateAsync(slot, _hierarchyRoot, ct);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        ISubjectErasureRegistry erasure,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(erasure);
        if (await erasure.IsErasedAsync(tenant, subject, ct).ConfigureAwait(false))
        {
            throw new SubjectErasedException(tenant.Value, subject.Value);
        }

        var tenantKey = await DeriveKeyAsync(tenant, purpose, ct).ConfigureAwait(false);
        var slot = BuildSubjectSlot(tenant, subject, purpose, CurrentKeyVersion);
        return await GetOrCreateAsync(slot, tenantKey, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> ResolveKeyAsync(
        TenantId tenant,
        string purpose,
        int keyVersion,
        CancellationToken ct)
    {
        if (keyVersion == CurrentKeyVersion)
        {
            return DeriveKeyAsync(tenant, purpose, ct);
        }
        if (keyVersion != LegacyKeyVersion)
        {
            throw new CryptographicException($"Unsupported stored key version {keyVersion}.");
        }

        return GetOrImportLegacyAsync(
            BuildTenantSlot(tenant, purpose, keyVersion),
            _hierarchyRoot,
            () => RequireLegacyProvider().DeriveKeyAsync(tenant, purpose, ct),
            ct);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> ResolveSubjectKeyAsync(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        int keyVersion,
        ISubjectErasureRegistry erasure,
        CancellationToken ct)
    {
        if (keyVersion == CurrentKeyVersion)
        {
            return await DeriveSubjectKeyAsync(tenant, subject, purpose, erasure, ct).ConfigureAwait(false);
        }
        if (keyVersion != LegacyKeyVersion)
        {
            throw new CryptographicException($"Unsupported stored key version {keyVersion}.");
        }
        if (await erasure.IsErasedAsync(tenant, subject, ct).ConfigureAwait(false))
        {
            throw new SubjectErasedException(tenant.Value, subject.Value);
        }

        var tenantKey = await ResolveKeyAsync(tenant, purpose, keyVersion, ct).ConfigureAwait(false);
        return await GetOrImportLegacyAsync(
            BuildSubjectSlot(tenant, subject, purpose, keyVersion),
            tenantKey,
            () => RequireLegacyProvider().DeriveSubjectKeyAsync(
                tenant,
                subject,
                purpose,
                erasure,
                ct),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct) =>
        _store.DeleteByPrefixAsync(BuildSubjectPrefix(tenant, subject), ct);

    /// <inheritdoc />
    public async Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct)
    {
        await _store.DeleteByPrefixAsync(BuildSubjectTenantPrefix(tenant), ct).ConfigureAwait(false);
        await _store.DeleteByPrefixAsync(BuildTenantPrefix(tenant), ct).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> GetOrCreateAsync(
        string slot,
        ReadOnlyMemory<byte> wrappingKey,
        CancellationToken ct)
    {
        var stored = await _store.ReadAsync(slot, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            return Unwrap(slot, stored.Value.Span, wrappingKey.Span);
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var wrapped = Wrap(slot, key, wrappingKey.Span);
        if (await _store.TryCreateAsync(slot, wrapped, ct).ConfigureAwait(false))
        {
            return key;
        }

        CryptographicOperations.ZeroMemory(key);
        stored = await _store.ReadAsync(slot, ct).ConfigureAwait(false);
        if (stored is null)
        {
            throw new InvalidOperationException($"Stored key slot '{slot}' disappeared during creation.");
        }

        return Unwrap(slot, stored.Value.Span, wrappingKey.Span);
    }

    private async Task<ReadOnlyMemory<byte>> GetOrImportLegacyAsync(
        string slot,
        ReadOnlyMemory<byte> wrappingKey,
        Func<Task<ReadOnlyMemory<byte>>> import,
        CancellationToken ct)
    {
        var stored = await _store.ReadAsync(slot, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            return Unwrap(slot, stored.Value.Span, wrappingKey.Span);
        }

        var legacyKey = await import().ConfigureAwait(false);
        var wrapped = Wrap(slot, legacyKey.Span, wrappingKey.Span);
        if (await _store.TryCreateAsync(slot, wrapped, ct).ConfigureAwait(false))
        {
            return legacyKey;
        }

        stored = await _store.ReadAsync(slot, ct).ConfigureAwait(false);
        if (stored is null)
        {
            throw new InvalidOperationException($"Stored legacy key slot '{slot}' disappeared during migration.");
        }

        return Unwrap(slot, stored.Value.Span, wrappingKey.Span);
    }

    private ITenantKeyProvider RequireLegacyProvider() =>
        _legacyProvider ?? throw new CryptographicException(
            "Legacy derived key material was not migrated and no legacy migration provider is available.");

    private static byte[] Wrap(string slot, ReadOnlySpan<byte> key, ReadOnlySpan<byte> wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[key.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(wrappingKey, TagLength))
        {
            aes.Encrypt(nonce, key, ciphertext, tag, Encoding.UTF8.GetBytes(slot));
        }

        var record = new byte[NonceLength + ciphertext.Length + TagLength];
        nonce.CopyTo(record, 0);
        ciphertext.CopyTo(record, NonceLength);
        tag.CopyTo(record, NonceLength + ciphertext.Length);
        return record;
    }

    private static byte[] Unwrap(string slot, ReadOnlySpan<byte> record, ReadOnlySpan<byte> wrappingKey)
    {
        if (record.Length != NonceLength + KeyLength + TagLength)
        {
            throw new CryptographicException("Stored key record has an invalid length.");
        }

        var plaintext = new byte[KeyLength];
        using (var aes = new AesGcm(wrappingKey, TagLength))
        {
            aes.Decrypt(
                record[..NonceLength],
                record.Slice(NonceLength, KeyLength),
                record[^TagLength..],
                plaintext,
                Encoding.UTF8.GetBytes(slot));
        }

        return plaintext;
    }

    private static string BuildTenantPrefix(TenantId tenant) =>
        $"tenant:{HashComponent(tenant.Value)}:";

    private static string BuildSubjectTenantPrefix(TenantId tenant) =>
        $"subject:{HashComponent(tenant.Value)}:";

    private static string BuildSubjectPrefix(TenantId tenant, SubjectId subject) =>
        $"{BuildSubjectTenantPrefix(tenant)}{HashComponent(subject.Value)}:";

    private static string BuildTenantSlot(TenantId tenant, string purpose, int keyVersion) =>
        $"{BuildTenantPrefix(tenant)}{HashComponent(purpose)}:v{keyVersion}";

    private static string BuildSubjectSlot(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        int keyVersion) =>
        $"{BuildSubjectPrefix(tenant, subject)}{HashComponent(purpose)}:v{keyVersion}";

    private static string HashComponent(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest);
    }
}
