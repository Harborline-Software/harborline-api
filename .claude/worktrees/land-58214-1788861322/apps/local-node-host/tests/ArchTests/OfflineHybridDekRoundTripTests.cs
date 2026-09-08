using System.Security.Cryptography;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Enrollment;

using Xunit;

using Ed25519Signer = Harborline.Api.Foundation.Crypto.Ed25519Signer;
using Ed25519Verifier = Harborline.Api.Foundation.Crypto.Ed25519Verifier;
using MemberRoster = Harborline.Api.Foundation.IdentityAtlas.MemberRoster;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// PQC Phase 2 / BL-01 increment <b>2c-iii-b — the OFFLINE-ACCEPTANCE gate</b> (ADR 0004 Amendment 2 GATE
/// condition 6: <i>"a Bridge-stopped offline install round-trips a hybrid-boxed DEK"</i>). Proves the suite-#3
/// (X-Wing) writer flip works with NO Bridge / NO network at all — the whole wrap → unwrap is local-node-secret
/// crypto over roster-bound keys. This is the local-first sovereignty property: a tenant DEK is hybrid-boxed for
/// an admitted, X-Wing-capable recipient and recovered by that recipient entirely offline.
/// </summary>
/// <remarks>
/// "Bridge-stopped offline" is realized by the test using ONLY local primitives + the host's in-memory
/// <see cref="NodeTeamRoster"/> — there is no <c>HttpClient</c>, no signal-bridge, no sync engine in the path. The
/// recipient's X-Wing PRIVATE key is HKDF-derived from its OWN root seed on its own node (never on the wire), and
/// the recipient X-Wing PUBLIC key the sender boxes to is the roster-bound key (PR-A), so the round-trip is the
/// real production construction minus the network.
/// </remarks>
public sealed class OfflineHybridDekRoundTripTests
{
    private static readonly IX25519KeyAgreement X25519 = new X25519KeyAgreement();
    private static readonly IXWingKem Xwing = new XWingKem();
    private static readonly IXWingSealedBox XwingBox = new XWingSealedBox(Xwing);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly HkdfXWingSubkeyDerivation XwingSubkey = new(Xwing);

    private static byte[] RandomSeed(byte salt)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(i + salt);
        return s;
    }

    private static byte[] FreshDek()
    {
        var dek = new byte[TenantDekWrapper.TenantDekLength];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact(DisplayName = "OFFLINE gate (GATE 6): a Bridge-stopped install round-trips a hybrid-boxed (suite-#3) DEK end-to-end")]
    public void Offline_RoundTrips_A_HybridBoxed_Dek()
    {
        // ── Build a verified roster (owner admits sally), all local — no Bridge. ──────────────────────────────
        var teamId = Guid.NewGuid();
        var teamIdStr = teamId.ToString("D");

        var ownerKp = KeyPair.Generate();
        var ownerSigner = new Ed25519Signer(ownerKp);
        var ownerRootSeed = RandomSeed(11);
        var ownerDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(ownerRootSeed, teamIdStr);

        var sallyKp = KeyPair.Generate();
        var sallyRootSeed = RandomSeed(97);
        var sallyDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(sallyRootSeed, teamIdStr);

        var member = MemberRoster
            .Genesis(teamId, "owner", ownerSigner, Verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid(),
                founderDmPublicKey: PrincipalId.FromBytes(ownerDmPub).ToBase64Url())
            .Admit("owner", ownerSigner, "sally", sallyKp.PrincipalId,
                Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionCompositions.Member,
                Verifier, DateTimeOffset.UnixEpoch.AddSeconds(1), Guid.NewGuid(),
                newDmPublicKey: PrincipalId.FromBytes(sallyDmPub).ToBase64Url());

        var roster = new NodeTeamRoster(member);
        roster.SetOwnDmPublicKey("sally", sallyDmPub);

        // ── Sally derives her X-Wing keypair from her OWN root seed (never on the wire) + the roster publishes
        //    her X-Wing PUBLIC key (PR-A). The host's resolver surfaces it (XWingPublicKeyOf). ─────────────────
        var sallyXWingSeed = XwingSubkey.DeriveXWingPrivateKeySeed(sallyRootSeed, teamIdStr);
        var sallyXWingPub = XwingSubkey.DeriveXWingPublicKey(sallyRootSeed, teamIdStr);
        roster.SetOwnXWingPublicKey("sally", sallyXWingPub);

        // The roster-bound X-Wing capability source (PR-A): sally IS X-Wing-capable.
        var resolvedSallyXWing = roster.XWingPublicKeyOf("sally");
        Assert.NotNull(resolvedSallyXWing);
        Assert.True(resolvedSallyXWing!.AsSpan().SequenceEqual(sallyXWingPub));

        // ── The WRITER (cutover ON) boxes the DEK as suite #3 for sally — offline. ────────────────────────────
        var policy = new ConfigurableHybridKemWritePolicy(enabled: true);
        var wrapper = new TenantDekWrapper(X25519, XwingBox, policy);
        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);

        var wrap = wrapper.Wrap(dek, recipientDmPublicKey: default, ctx, "owner", ownerSigner, resolvedSallyXWing);
        Assert.Equal(KemSuite.XWingX25519MlKem768_v1, wrap.Suite); // hybrid-boxed.

        // ── Sally opens it OFFLINE with her OWN root-derived X-Wing seed (no Bridge, no network). ─────────────
        var recovered = wrapper.VerifyAndUnwrap(
            wrap, ownerSigner.IssuerId, ctx, recipientDmPrivateKey: default, Verifier, sallyXWingSeed);

        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered); // the offline install round-tripped the hybrid-boxed DEK.
    }

    [Fact(DisplayName = "OFFLINE gate: an X-Wing-INCAPABLE admitted recipient still round-trips a suite-#1 DEK offline (safe degrade)")]
    public void Offline_IncapableRecipient_RoundTrips_Suite1()
    {
        var teamId = Guid.NewGuid();
        var teamIdStr = teamId.ToString("D");
        var ownerKp = KeyPair.Generate();
        var ownerSigner = new Ed25519Signer(ownerKp);
        var ownerRootSeed = RandomSeed(11);
        var ownerDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(ownerRootSeed, teamIdStr);
        var sallyKp = KeyPair.Generate();
        var sallyRootSeed = RandomSeed(97);
        var sallyDmPub = NodeDmKeyDerivation.DeriveDmPublicKey(sallyRootSeed, teamIdStr);

        var member = MemberRoster
            .Genesis(teamId, "owner", ownerSigner, Verifier, DateTimeOffset.UnixEpoch, Guid.NewGuid(),
                founderDmPublicKey: PrincipalId.FromBytes(ownerDmPub).ToBase64Url())
            .Admit("owner", ownerSigner, "sally", sallyKp.PrincipalId,
                Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionCompositions.Member,
                Verifier, DateTimeOffset.UnixEpoch.AddSeconds(1), Guid.NewGuid(),
                newDmPublicKey: PrincipalId.FromBytes(sallyDmPub).ToBase64Url());
        var roster = new NodeTeamRoster(member);
        roster.SetOwnDmPublicKey("sally", sallyDmPub);

        // Sally published NO X-Wing key → not X-Wing-capable. The resolver returns null.
        Assert.Null(roster.XWingPublicKeyOf("sally"));
        var sallyWrapKey = roster.DmPublicKeyOf("sally");
        Assert.NotNull(sallyWrapKey);

        // Even with the cutover ON, the writer boxes suite #1 for the incapable recipient (safe degrade) — and it
        // round-trips offline under sally's DM private key.
        var policy = new ConfigurableHybridKemWritePolicy(enabled: true);
        var wrapper = new TenantDekWrapper(X25519, XwingBox, policy);
        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);
        var wrap = wrapper.Wrap(dek, sallyWrapKey!, ctx, "owner", ownerSigner);
        Assert.Equal(KemSuite.X25519SealedBox_v1, wrap.Suite);

        var sallyDmPriv = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamIdStr);
        var recovered = wrapper.VerifyAndUnwrap(wrap, ownerSigner.IssuerId, ctx, sallyDmPriv, Verifier);
        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered);
    }
}
