using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost;

/// <summary>Derives the complete replacement-key set after a root-seed recovery or rotation.</summary>
public static class LocalNodeDerivedKeyRekeyer
{
    /// <summary>Derive replacement material for every currently inventoried root-derived domain.</summary>
    /// <param name="replacementRootSeed">New 32-byte install root seed.</param>
    /// <param name="teamId">Team whose derived material is being replaced.</param>
    /// <param name="tenantId">Tenant whose content key is being replaced.</param>
    /// <param name="subjectId">Subject whose content sub-key is being replaced.</param>
    /// <param name="purpose">Tenant-content purpose label.</param>
    /// <param name="erasureRegistry">Erasure authority consulted before subject-key derivation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The complete replacement-key set.</returns>
    public static async Task<LocalNodeDerivedKeySet> DeriveReplacementKeysAsync(
        ReadOnlyMemory<byte> replacementRootSeed,
        string teamId,
        TenantId tenantId,
        SubjectId subjectId,
        string purpose,
        ISubjectErasureRegistry erasureRegistry,
        CancellationToken ct)
    {
        if (replacementRootSeed.Length != 32)
        {
            throw new ArgumentException(
                $"Replacement root seed must be 32 bytes (was {replacementRootSeed.Length}).",
                nameof(replacementRootSeed));
        }
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(erasureRegistry);
        ct.ThrowIfCancellationRequested();

        var signer = new Ed25519Signer();
        var teamSubkey = new TeamSubkeyDerivation(signer)
            .DeriveSubkey(replacementRootSeed.Span, teamId);
        var tenantKeys = new RootSeedTenantKeyProvider(replacementRootSeed.Span);
        var tenantDek = await tenantKeys
            .DeriveKeyAsync(tenantId, purpose, ct)
            .ConfigureAwait(false);
        var subjectDek = await tenantKeys
            .DeriveSubjectKeyAsync(tenantId, subjectId, purpose, erasureRegistry, ct)
            .ConfigureAwait(false);

        return new LocalNodeDerivedKeySet(
            replacementRootSeed.ToArray(),
            teamSubkey.AsSpan(0, 32).ToArray(),
            new SqlCipherKeyDerivation().DeriveSqlCipherKey(replacementRootSeed.Span, teamId),
            new HkdfX25519SubkeyDerivation().DeriveX25519PrivateKey(replacementRootSeed, teamId),
            NodeDmKeyDerivation.DeriveDmPrivateKey(replacementRootSeed.Span, teamId),
            new HkdfXWingSubkeyDerivation(new XWingKem())
                .DeriveXWingPrivateKeySeed(replacementRootSeed, teamId),
            tenantDek.ToArray(),
            subjectDek.ToArray());
    }
}

/// <summary>Replacement key material covering every root-derived domain in ticket 043's inventory.</summary>
public sealed class LocalNodeDerivedKeySet
{
    internal LocalNodeDerivedKeySet(
        byte[] rootIdentitySeed,
        byte[] teamSigningSeed,
        byte[] sqlCipherKey,
        byte[] recoveryX25519PrivateKey,
        byte[] dmX25519PrivateKey,
        byte[] xWingPrivateKeySeed,
        byte[] tenantDek,
        byte[] subjectDek)
    {
        RootIdentitySeed = rootIdentitySeed;
        TeamSigningSeed = teamSigningSeed;
        SqlCipherKey = sqlCipherKey;
        RecoveryX25519PrivateKey = recoveryX25519PrivateKey;
        DmX25519PrivateKey = dmX25519PrivateKey;
        XWingPrivateKeySeed = xWingPrivateKeySeed;
        TenantDek = tenantDek;
        SubjectDek = subjectDek;
    }

    /// <summary>Replacement root Ed25519 identity seed.</summary>
    public byte[] RootIdentitySeed { get; }

    /// <summary>Replacement per-team Ed25519 signing seed.</summary>
    public byte[] TeamSigningSeed { get; }

    /// <summary>Replacement per-team SQLCipher key.</summary>
    public byte[] SqlCipherKey { get; }

    /// <summary>Replacement per-team recovery X25519 private key.</summary>
    public byte[] RecoveryX25519PrivateKey { get; }

    /// <summary>Replacement per-team direct-message X25519 private key.</summary>
    public byte[] DmX25519PrivateKey { get; }

    /// <summary>Replacement per-team X-Wing private-key seed.</summary>
    public byte[] XWingPrivateKeySeed { get; }

    /// <summary>Replacement tenant content-encryption key.</summary>
    public byte[] TenantDek { get; }

    /// <summary>Replacement subject content-encryption sub-key.</summary>
    public byte[] SubjectDek { get; }
}
