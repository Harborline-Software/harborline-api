using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.DependencyInjection;
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
/// PQC Phase 2 / BL-01 increment <b>2c-iii-c — the host-composition ENABLE</b> (ADR 0004 Amendment 2 GATE
/// condition 5). Proves the <c>apps/local-node-host</c> composition root turns the 2c-iii-b writer flip ON from a
/// per-install config flag, and that BOTH kill-switch paths (config-off + the env force-off) cleanly revert the
/// host-composed writer to suite #1 with the read path still accepting suite #3 (nothing stranded).
/// </summary>
/// <remarks>
/// <para>
/// These tests assert against the ACTUAL host wiring (<see cref="NodeHybridKemWritePolicyComposition"/> over
/// kernel-security's <c>AddHarborlineKernelSecurity</c> + the MD-1 pairing composition), not the kernel-security
/// primitive in isolation (that round-trip is proven in <c>XWingWriterFlipTests</c> /
/// <c>OfflineHybridDekRoundTripTests</c>). The point of THIS file is the COMPOSITION: a host that reads
/// <c>LocalNode:HybridKemWrite:Enabled=true</c> resolves an ENABLED policy and its DI-resolved
/// <see cref="TenantDekWrapper"/> emits suite #3 for a capable recipient; a host that does not (or that engages the
/// env kill-switch) resolves a DISABLED policy and the same DI-resolved wrapper boxes suite #1.
/// </para>
/// </remarks>

// Mutates the process-wide kill-switch, so this class shares the serialized environment collection.
[Collection("Hybrid KEM write policy environment")]
public sealed class HybridKemWritePolicyCompositionTests : IDisposable
{
    private static readonly IXWingKem Xwing = new XWingKem();
    private static readonly IXWingSealedBox XwingBox = new XWingSealedBox(Xwing);
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly HkdfXWingSubkeyDerivation XwingSubkey = new(Xwing);

    // Capture + restore the ambient env kill-switch so a test that sets it cannot leak into the next test.
    private readonly string? _savedKillSwitch =
        Environment.GetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(
            NodeHybridKemWritePolicyComposition.DisableEnvVarName, _savedKillSwitch);

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Build the host DI graph exactly as the composition root does — kernel-security primitives (which TryAdd the
    /// fail-closed <see cref="DisabledHybridKemWritePolicy"/> default + the policy-injecting <see cref="TenantDekWrapper"/>),
    /// the MD-1 pairing composition, then the 2c-iii-c enable that <c>Replace</c>s the policy. Returns the built
    /// provider so a test resolves the SAME <see cref="ITenantDekWrapper"/>/<see cref="IHybridKemWritePolicy"/> the
    /// production host would.
    /// </summary>
    private static ServiceProvider BuildHostGraph(bool configEnabled)
    {
        var services = new ServiceCollection();
        services.AddHarborlineKernelSecurity();        // registers DisabledHybridKemWritePolicy (TryAdd) + TenantDekWrapper
        services.AddNodeTenantDekPairing();          // idempotent wrap construction
        services.AddNodeHybridKemWritePolicy(configEnabled); // the 2c-iii-c enable (Replace)
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A verified roster: founder "owner" admits "sally" (DM key signed in), and sally publishes her roster-bound
    /// X-Wing public key (PR-A) → she IS X-Wing-capable. Returns the roster, the admin signer, sally's root seed (so
    /// the test can derive her private X-Wing seed + DM private key to OPEN), and the team id string.
    /// </summary>
    private static (NodeTeamRoster roster, Ed25519Signer adminSigner, byte[] sallyRootSeed, string teamId)
        BuildRosterWithCapableSally()
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

        var sallyXWingPub = XwingSubkey.DeriveXWingPublicKey(sallyRootSeed, teamIdStr);
        roster.SetOwnXWingPublicKey("sally", sallyXWingPub);

        return (roster, ownerSigner, sallyRootSeed, teamIdStr);
    }

    // ── FENCE 1 — config flag drives the resolved policy ───────────────────────────────────────────────────────

    [Fact(DisplayName = "2c-iii-c: kernel-security ALONE resolves the FAIL-CLOSED DisabledHybridKemWritePolicy (suite #1 default)")]
    public void KernelSecurityAlone_ResolvesFailClosedDefault()
    {
        var services = new ServiceCollection();
        services.AddHarborlineKernelSecurity();
        using var sp = services.BuildServiceProvider();

        var policy = sp.GetRequiredService<IHybridKemWritePolicy>();
        Assert.IsType<DisabledHybridKemWritePolicy>(policy);
        Assert.False(policy.HybridWritesEnabled); // the safe pre-cutover state.
    }

    [Fact(DisplayName = "2c-iii-c: config Enabled=true → the host graph resolves an ENABLED policy (the cutover ON)")]
    public void ConfigEnabled_ResolvesEnabledPolicy()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);

        using var sp = BuildHostGraph(configEnabled: true);
        var policy = sp.GetRequiredService<IHybridKemWritePolicy>();

        Assert.True(policy.HybridWritesEnabled);
        Assert.IsNotType<DisabledHybridKemWritePolicy>(policy); // the enable Replaced the fail-closed default.
    }

    [Fact(DisplayName = "2c-iii-c (KILL-SWITCH a): config Enabled=false → the host graph resolves a DISABLED policy (suite #1)")]
    public void ConfigDisabled_ResolvesDisabledPolicy()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);

        using var sp = BuildHostGraph(configEnabled: false);
        var policy = sp.GetRequiredService<IHybridKemWritePolicy>();

        Assert.False(policy.HybridWritesEnabled);
    }

    [Theory(DisplayName = "2c-iii-c (KILL-SWITCH b): the env kill-switch FORCES the policy OFF even when config enables it")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData("On")]
    public void EnvKillSwitch_ForcesOff_EvenWhenConfigEnabled(string truthy)
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, truthy);

        using var sp = BuildHostGraph(configEnabled: true); // config says ON …
        var policy = sp.GetRequiredService<IHybridKemWritePolicy>();

        Assert.False(policy.HybridWritesEnabled); // … but the env kill-switch wins (no-redeploy incident revert).
    }

    [Fact(DisplayName = "2c-iii-c: a non-truthy env kill-switch value does NOT disable an enabled config")]
    public void EnvKillSwitch_NonTruthy_DoesNotDisable()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, "false");

        using var sp = BuildHostGraph(configEnabled: true);
        var policy = sp.GetRequiredService<IHybridKemWritePolicy>();

        Assert.True(policy.HybridWritesEnabled);
    }

    // ── FENCE 2 — the host-COMPOSED writer behaves per the resolved policy (end-to-end) ────────────────────────

    [Fact(DisplayName = "2c-iii-c: config ON + CAPABLE recipient → the DI-resolved writer emits suite #3, round-trips end-to-end")]
    public void HostWriter_ConfigOn_Capable_EmitsSuite3_RoundTrips()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);
        var (roster, ownerSigner, sallyRootSeed, teamIdStr) = BuildRosterWithCapableSally();

        using var sp = BuildHostGraph(configEnabled: true);
        var wrapper = sp.GetRequiredService<TenantDekWrapper>();

        var recipientXWingPub = roster.XWingPublicKeyOf("sally");
        Assert.NotNull(recipientXWingPub); // sally is X-Wing-capable (roster-bound key, PR-A).

        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);

        // WRITE through the host-composed wrapper — policy ON + capable ⇒ suite #3.
        var wrap = wrapper.Wrap(dek, recipientDmPublicKey: default, ctx, "owner", ownerSigner, recipientXWingPub);
        Assert.Equal(KemSuite.XWingX25519MlKem768_v1, wrap.Suite);

        // OPEN with sally's own root-derived X-Wing seed.
        var sallyXWingSeed = XwingSubkey.DeriveXWingPrivateKeySeed(sallyRootSeed, teamIdStr);
        var recovered = wrapper.VerifyAndUnwrap(
            wrap, ownerSigner.IssuerId, ctx, recipientDmPrivateKey: default, Verifier, sallyXWingSeed);
        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered);
    }

    [Fact(DisplayName = "2c-iii-c: config ON + NON-capable recipient → the DI-resolved writer SAFELY DEGRADES to suite #1, round-trips")]
    public void HostWriter_ConfigOn_Incapable_DegradesToSuite1_RoundTrips()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);

        // A roster where sally is admitted but publishes NO X-Wing key → not X-Wing-capable.
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

        Assert.Null(roster.XWingPublicKeyOf("sally")); // not capable.
        var sallyWrapKey = roster.DmPublicKeyOf("sally");
        Assert.NotNull(sallyWrapKey);

        using var sp = BuildHostGraph(configEnabled: true);
        var wrapper = sp.GetRequiredService<TenantDekWrapper>();

        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);

        // Even with the cutover ON, an incapable recipient (no X-Wing key supplied) gets suite #1 — never stranded.
        var wrap = wrapper.Wrap(dek, sallyWrapKey!, ctx, "owner", ownerSigner);
        Assert.Equal(KemSuite.X25519SealedBox_v1, wrap.Suite);

        var sallyDmPriv = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamIdStr);
        var recovered = wrapper.VerifyAndUnwrap(wrap, ownerSigner.IssuerId, ctx, sallyDmPriv, Verifier);
        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered);
    }

    [Fact(DisplayName = "2c-iii-c (KILL-SWITCH proven): cutover OFF → the DI-resolved writer reverts to suite #1, AND a previously-emitted suite-#3 box still OPENS (nothing stranded)")]
    public void HostWriter_KillSwitch_RevertsToSuite1_While_Suite3_StillReadable()
    {
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);
        var (roster, ownerSigner, sallyRootSeed, teamIdStr) = BuildRosterWithCapableSally();
        var recipientXWingPub = roster.XWingPublicKeyOf("sally")!;
        var sallyXWingSeed = XwingSubkey.DeriveXWingPrivateKeySeed(sallyRootSeed, teamIdStr);

        // ── Phase 1: cutover ON — emit a suite-#3 box for sally (the "already-emitted #3 material"). ───────────
        WrappedTenantDek priorSuite3Wrap;
        var priorDek = FreshDek();
        var priorCtx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);
        using (var onSp = BuildHostGraph(configEnabled: true))
        {
            var onWrapper = onSp.GetRequiredService<TenantDekWrapper>();
            priorSuite3Wrap = onWrapper.Wrap(
                priorDek, recipientDmPublicKey: default, priorCtx, "owner", ownerSigner, recipientXWingPub);
            Assert.Equal(KemSuite.XWingX25519MlKem768_v1, priorSuite3Wrap.Suite);
        }

        // ── Phase 2: KILL-SWITCH — cutover OFF. A FRESH wrap reverts to suite #1 even for the CAPABLE recipient. ─
        using var offSp = BuildHostGraph(configEnabled: false);
        var offPolicy = offSp.GetRequiredService<IHybridKemWritePolicy>();
        Assert.False(offPolicy.HybridWritesEnabled);

        var offWrapper = offSp.GetRequiredService<TenantDekWrapper>();
        var newDek = FreshDek();
        var newCtx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 1);
        var sallyWrapKey = roster.DmPublicKeyOf("sally")!;
        // With the cutover off, even supplying the X-Wing key the writer boxes suite #1 (the policy gate is false).
        var revertedWrap = offWrapper.Wrap(
            newDek, sallyWrapKey, newCtx, "owner", ownerSigner, recipientXWingPub);
        Assert.Equal(KemSuite.X25519SealedBox_v1, revertedWrap.Suite); // reverted to suite #1, nothing stranded.

        // The reverted suite-#1 box round-trips under sally's DM private key.
        var sallyDmPriv = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamIdStr);
        var revertedRecovered = offWrapper.VerifyAndUnwrap(
            revertedWrap, ownerSigner.IssuerId, newCtx, sallyDmPriv, Verifier);
        Assert.NotNull(revertedRecovered);
        Assert.Equal(newDek, revertedRecovered);

        // AND — the load-bearing kill-switch property — the read path on the SAME post-kill graph STILL opens the
        // previously-emitted suite-#3 box (read-before-write keeps #3 readable; the kill-switch only stops NEW #3
        // writes). So flipping the kill-switch strands nothing.
        var priorRecovered = offWrapper.VerifyAndUnwrap(
            priorSuite3Wrap, ownerSigner.IssuerId, priorCtx, recipientDmPrivateKey: default, Verifier, sallyXWingSeed);
        Assert.NotNull(priorRecovered);
        Assert.Equal(priorDek, priorRecovered);
    }
}
