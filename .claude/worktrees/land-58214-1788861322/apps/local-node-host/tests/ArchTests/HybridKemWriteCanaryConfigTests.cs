using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.DependencyInjection;
using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Enrollment;

using Xunit;

using Ed25519Signer = Harborline.Api.Foundation.Crypto.Ed25519Signer;
using Ed25519Verifier = Harborline.Api.Foundation.Crypto.Ed25519Verifier;
using MemberRoster = Harborline.Api.Foundation.IdentityAtlas.MemberRoster;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// PQC Phase 2 / BL-01 increment <b>2c-iii-d — the CANARY FLIP</b> (ADR 0004 Amendment 2, staged rollout). Proves
/// the cutover is LIVE on the dev/reference (canary) build via the host's REAL config files — and that the
/// production base default stays OFF.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="HybridKemWritePolicyCompositionTests"/> drives the composition from a <c>bool</c> (the unit
/// gate), THIS file drives it from the ACTUAL host <c>appsettings.json</c> (base, OFF) layered with the canary
/// overlay <c>appsettings.Development.json</c> (<c>LocalNode:HybridKemWrite:Enabled=true</c>) — both copied into the
/// test output from the host project so the proof can never drift from what production ships. It binds
/// <see cref="LocalNodeOptions"/> exactly as <c>Program.cs</c> does (<c>GetSection("LocalNode").Bind(...)</c>) and
/// feeds the bound flag into <see cref="NodeHybridKemWritePolicyComposition.AddNodeHybridKemWritePolicy"/>, then
/// exercises the REAL DI-resolved <see cref="TenantDekWrapper"/> wrap path. This is the "config-on integration proof
/// that exercises the actual wrap-with-enabled-policy path", not just the unit gate.
/// </para>
/// <para>
/// What it locks down:
/// <list type="bullet">
///   <item><b>Prod default OFF</b> — base <c>appsettings.json</c> ALONE binds <c>Enabled=false</c> (an un-opted-in
///         install boxes suite #1; the HNDL hole stays closed-by-not-being-touched, no fleet-wide flip).</item>
///   <item><b>Canary ON</b> — base + <c>appsettings.Development.json</c> overlay binds <c>Enabled=true</c>, and the
///         host-composed writer emits suite #3 (standard X-Wing) for an X-Wing-capable recipient + round-trips.</item>
///   <item><b>Safe degrade under canary</b> — a NON-capable recipient still gets suite #1 (never stranded).</item>
///   <item><b>Kill-switch under canary</b> — with the canary config ON, the env force-off
///         (<c>HARBORLINE_PQC_HYBRID_WRITE_DISABLED</c>) reverts a fresh write to suite #1 AND a previously-emitted
///         suite-#3 box still OPENS (nothing stranded).</item>
/// </list>
/// </para>
/// </remarks>

// Mutates the process-wide kill-switch, so this class shares the serialized environment collection.
[Collection("Hybrid KEM write policy environment")]
public sealed class HybridKemWriteCanaryConfigTests : IDisposable
{
    private static readonly IXWingKem Xwing = new XWingKem();
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly HkdfXWingSubkeyDerivation XwingSubkey = new(Xwing);

    private readonly string? _savedKillSwitch =
        Environment.GetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName);

    public HybridKemWriteCanaryConfigTests() =>
        // Default the env kill-switch OFF so the config layering is what decides the outcome (a test that needs it
        // set does so explicitly).
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(
            NodeHybridKemWritePolicyComposition.DisableEnvVarName, _savedKillSwitch);

    // ── Config loaders — the ACTUAL host appsettings files, bound exactly as Program.cs does ─────────────────────

    private static readonly string AppSettingsDir = AppContext.BaseDirectory;

    /// <summary>Base config only — mirrors a production / un-opted-in install (no Development overlay).</summary>
    private static LocalNodeOptions BindBaseOnly()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppSettingsDir)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var options = new LocalNodeOptions();
        config.GetSection("LocalNode").Bind(options);
        return options;
    }

    /// <summary>Base + the canary Development overlay — mirrors the dev/reference (canary) build.</summary>
    private static LocalNodeOptions BindCanary()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppSettingsDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();
        var options = new LocalNodeOptions();
        config.GetSection("LocalNode").Bind(options);
        return options;
    }

    private static ServiceProvider BuildHostGraph(bool configEnabled)
    {
        var services = new ServiceCollection();
        services.AddHarborlineKernelSecurity();
        services.AddNodeTenantDekPairing();
        services.AddNodeHybridKemWritePolicy(configEnabled);
        return services.BuildServiceProvider();
    }

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

    // ── PROOF 1 — the config layering: prod default OFF, canary ON ───────────────────────────────────────────────

    [Fact(DisplayName = "CANARY: production base appsettings.json ALONE binds HybridKemWrite:Enabled=false (prod default stays OFF)")]
    public void BaseConfigAlone_BindsDisabled_ProdDefaultStaysOff()
    {
        var options = BindBaseOnly();
        Assert.False(options.HybridKemWrite.Enabled); // an un-opted-in install boxes suite #1 — no fleet-wide flip.
    }

    [Fact(DisplayName = "CANARY: base + appsettings.Development.json overlay binds HybridKemWrite:Enabled=true (the canary flip is live in config)")]
    public void CanaryConfig_BindsEnabled()
    {
        var options = BindCanary();
        Assert.True(options.HybridKemWrite.Enabled); // the dev/reference (canary) build opts in.
    }

    // ── PROOF 2 — the canary config ON drives the REAL writer to suite #3 + round-trips ──────────────────────────

    [Fact(DisplayName = "CANARY (live proof): canary config ON + capable recipient → the DI-resolved writer emits suite #3, round-trips end-to-end")]
    public void CanaryConfig_Capable_EmitsSuite3_RoundTrips()
    {
        var options = BindCanary();
        Assert.True(options.HybridKemWrite.Enabled);

        var (roster, ownerSigner, sallyRootSeed, teamIdStr) = BuildRosterWithCapableSally();

        // Feed the BOUND canary flag into the composition exactly as Program.cs does.
        using var sp = BuildHostGraph(options.HybridKemWrite.Enabled);
        var wrapper = sp.GetRequiredService<TenantDekWrapper>();

        var recipientXWingPub = roster.XWingPublicKeyOf("sally");
        Assert.NotNull(recipientXWingPub);

        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);

        var wrap = wrapper.Wrap(dek, recipientDmPublicKey: default, ctx, "owner", ownerSigner, recipientXWingPub);
        Assert.Equal(KemSuite.XWingX25519MlKem768_v1, wrap.Suite); // THE EVIDENCE: a real canary wrap is suite #3.

        var sallyXWingSeed = XwingSubkey.DeriveXWingPrivateKeySeed(sallyRootSeed, teamIdStr);
        var recovered = wrapper.VerifyAndUnwrap(
            wrap, ownerSigner.IssuerId, ctx, recipientDmPrivateKey: default, Verifier, sallyXWingSeed);
        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered); // the capable recipient OPENS it — unwrap health holds.
    }

    [Fact(DisplayName = "CANARY (safe degrade): canary config ON + NON-capable recipient → safely degrades to suite #1, round-trips")]
    public void CanaryConfig_Incapable_DegradesToSuite1_RoundTrips()
    {
        var options = BindCanary();
        Assert.True(options.HybridKemWrite.Enabled);

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

        using var sp = BuildHostGraph(options.HybridKemWrite.Enabled);
        var wrapper = sp.GetRequiredService<TenantDekWrapper>();

        var dek = FreshDek();
        var ctx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);

        var wrap = wrapper.Wrap(dek, sallyWrapKey!, ctx, "owner", ownerSigner);
        Assert.Equal(KemSuite.X25519SealedBox_v1, wrap.Suite); // canary ON, but incapable ⇒ suite #1 (never stranded).

        var sallyDmPriv = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamIdStr);
        var recovered = wrapper.VerifyAndUnwrap(wrap, ownerSigner.IssuerId, ctx, sallyDmPriv, Verifier);
        Assert.NotNull(recovered);
        Assert.Equal(dek, recovered);
    }

    // ── PROOF 3 — the kill-switch is live UNDER the canary config ────────────────────────────────────────────────

    [Fact(DisplayName = "CANARY (kill-switch live): canary config ON + env HARBORLINE_PQC_HYBRID_WRITE_DISABLED=1 → fresh write reverts to suite #1, AND a previously-emitted suite-#3 box still OPENS (nothing stranded)")]
    public void CanaryConfig_EnvKillSwitch_RevertsToSuite1_While_PriorSuite3_StillReadable()
    {
        var options = BindCanary();
        Assert.True(options.HybridKemWrite.Enabled); // config still says ON …

        var (roster, ownerSigner, sallyRootSeed, teamIdStr) = BuildRosterWithCapableSally();
        var recipientXWingPub = roster.XWingPublicKeyOf("sally")!;
        var sallyXWingSeed = XwingSubkey.DeriveXWingPrivateKeySeed(sallyRootSeed, teamIdStr);

        // ── Phase 1: canary ON (env clear) — emit a suite-#3 box (the "already-emitted #3 material"). ───────────
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, null);
        WrappedTenantDek priorSuite3Wrap;
        var priorDek = FreshDek();
        var priorCtx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 0);
        using (var onSp = BuildHostGraph(options.HybridKemWrite.Enabled))
        {
            var onWrapper = onSp.GetRequiredService<TenantDekWrapper>();
            priorSuite3Wrap = onWrapper.Wrap(
                priorDek, recipientDmPublicKey: default, priorCtx, "owner", ownerSigner, recipientXWingPub);
            Assert.Equal(KemSuite.XWingX25519MlKem768_v1, priorSuite3Wrap.Suite);
        }

        // ── Phase 2: ENGAGE the env kill-switch — config STILL ON, but the env force-off wins (no-redeploy). ────
        Environment.SetEnvironmentVariable(NodeHybridKemWritePolicyComposition.DisableEnvVarName, "1");
        using var offSp = BuildHostGraph(options.HybridKemWrite.Enabled); // same canary config flag, env now forces off
        var offPolicy = offSp.GetRequiredService<IHybridKemWritePolicy>();
        Assert.False(offPolicy.HybridWritesEnabled); // env kill-switch beats the canary config.

        var offWrapper = offSp.GetRequiredService<TenantDekWrapper>();
        var newDek = FreshDek();
        var newCtx = TenantDekPairingContext.For(teamIdStr, "sally", homeEpoch: 1);
        var sallyWrapKey = roster.DmPublicKeyOf("sally")!;
        var revertedWrap = offWrapper.Wrap(newDek, sallyWrapKey, newCtx, "owner", ownerSigner, recipientXWingPub);
        Assert.Equal(KemSuite.X25519SealedBox_v1, revertedWrap.Suite); // fresh write reverted to suite #1.

        var sallyDmPriv = NodeDmKeyDerivation.DeriveDmPrivateKey(sallyRootSeed, teamIdStr);
        var revertedRecovered = offWrapper.VerifyAndUnwrap(
            revertedWrap, ownerSigner.IssuerId, newCtx, sallyDmPriv, Verifier);
        Assert.NotNull(revertedRecovered);
        Assert.Equal(newDek, revertedRecovered);

        // The previously-emitted suite-#3 box STILL opens on the kill-switched graph — nothing stranded.
        var priorRecovered = offWrapper.VerifyAndUnwrap(
            priorSuite3Wrap, ownerSigner.IssuerId, priorCtx, recipientDmPrivateKey: default, Verifier, sallyXWingSeed);
        Assert.NotNull(priorRecovered);
        Assert.Equal(priorDek, priorRecovered);
    }
}
