using System;
using System.Collections.Generic;
using System.Text;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Feed;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Feed;

/// <summary>
/// U3 — the §5.3 "update available" derivation (<see cref="PackUpdateStateDeriver.Derive"/>): pure,
/// I/O-free unit coverage of every state in the table, including BOTH directions of the UF-4 epoch gate
/// (§6.4 — a version whose <c>MinNodeEpoch</c> is at-or-below the running counter proceeds normally; one
/// above it is blocked, regardless of every other signal). Route-level wiring (the SAME derivation reached
/// through <c>POST /channels/{id}/check</c>) is covered by <see cref="ChannelFeedRouteTests"/>.
/// </summary>
public sealed class PackUpdateStateDeriverTests
{
    private const string PackKey = "harborline.test-pack";

    [Fact(DisplayName = "U3: latest == installed → UpToDate, no affordance")]
    public void Derive_up_to_date()
    {
        var staged = Staged(latest: "2.0.0", minNodeEpoch: 1);
        var active = Installed("2.0.0");
        var preview = Preview("2.0.0", isUpgrade: true, priorVersion: "2.0.0");

        Assert.Equal(PackUpdateState.UpToDate, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3: latest > installed, no watermark hits → UpdateAvailable(vN)")]
    public void Derive_update_available()
    {
        var staged = Staged(latest: "3.0.0", minNodeEpoch: 1);
        var active = Installed("2.0.0");
        var preview = Preview("3.0.0", isUpgrade: true, priorVersion: "2.0.0");

        Assert.Equal(PackUpdateState.UpdateAvailable, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3: latest > installed but a safety floor would weaken → AvailableButBlocked (break-glass only)")]
    public void Derive_available_but_blocked_on_floor_weaken()
    {
        var staged = Staged(latest: "3.0.0", minNodeEpoch: 1);
        var active = Installed("2.0.0");
        var preview = Preview(
            "3.0.0", isUpgrade: true, priorVersion: "2.0.0",
            watermarkHits: new[] { new PackWatermarkHit(PackWatermarkHitKind.FloorWeakened, "floor weakened") });

        Assert.Equal(PackUpdateState.AvailableButBlocked, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3: latest < installed (e.g. after a yank) → Downgrade, never offered")]
    public void Derive_downgrade_not_offered()
    {
        var staged = Staged(latest: "1.0.0", minNodeEpoch: 1);
        var active = Installed("2.0.0");
        var preview = Preview(
            "1.0.0", isUpgrade: true, priorVersion: "2.0.0",
            watermarkHits: new[] { new PackWatermarkHit(PackWatermarkHitKind.VersionDowngrade, "downgrade") });

        Assert.Equal(PackUpdateState.Downgrade, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3: pack is feed-only, nothing installed → NotInstalled")]
    public void Derive_not_installed()
    {
        var staged = Staged(latest: "1.0.0", minNodeEpoch: 1);
        var preview = Preview("1.0.0", isUpgrade: false, priorVersion: null);

        Assert.Equal(PackUpdateState.NotInstalled, PackUpdateStateDeriver.Derive(staged, active: null, preview));
    }

    // ── UF-4 epoch gate — BOTH directions ──────────────────────────────────────────────────────────

    [Fact(DisplayName = "U3 epoch gate (at-or-below): MinNodeEpoch == NodeKernelEpoch.Current does NOT block — normal derivation proceeds")]
    public void Derive_epoch_at_current_does_not_block()
    {
        var staged = Staged(latest: "3.0.0", minNodeEpoch: NodeKernelEpoch.Current);
        var active = Installed("2.0.0");
        var preview = Preview("3.0.0", isUpgrade: true, priorVersion: "2.0.0");

        Assert.Equal(PackUpdateState.UpdateAvailable, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3 epoch gate (above): MinNodeEpoch > NodeKernelEpoch.Current → NeedsNewerNode, overrides every other signal")]
    public void Derive_epoch_above_current_blocks_even_a_clean_upgrade()
    {
        var staged = Staged(latest: "3.0.0", minNodeEpoch: NodeKernelEpoch.Current + 1);
        var active = Installed("2.0.0");
        var preview = Preview("3.0.0", isUpgrade: true, priorVersion: "2.0.0"); // otherwise a clean upgrade

        Assert.Equal(PackUpdateState.NeedsNewerNode, PackUpdateStateDeriver.Derive(staged, active, preview));
    }

    [Fact(DisplayName = "U3 epoch gate (above) even when NOT installed at all → NeedsNewerNode, not NotInstalled")]
    public void Derive_epoch_above_current_blocks_a_fresh_install_too()
    {
        var staged = Staged(latest: "1.0.0", minNodeEpoch: NodeKernelEpoch.Current + 1);
        var preview = Preview("1.0.0", isUpgrade: false, priorVersion: null);

        Assert.Equal(PackUpdateState.NeedsNewerNode, PackUpdateStateDeriver.Derive(staged, active: null, preview));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static StagedFeedPack Staged(string latest, long minNodeEpoch)
        => new(PackKey, latest, minNodeEpoch, Cid.FromBytes(Encoding.UTF8.GetBytes(latest)), ReadOnlyMemory<byte>.Empty);

    private static InstalledPack Installed(string version)
        => new(
            PackKey, version, PackScopeTier.Horizontal, PackLifecycleState.Active,
            SeedItems: Array.Empty<PackSeedItem>(),
            SafetyFloors: new Dictionary<string, int>(),
            InstalledAtUtc: DateTimeOffset.UtcNow,
            SignerKeyId: default,
            Epoch: 1,
            VouchingScope: TrustScope.HarborlineChannel,
            Dependencies: Array.Empty<PackDependencyRef>());

    private static PackInstallPreview Preview(
        string version, bool isUpgrade, string? priorVersion,
        IReadOnlyList<PackWatermarkHit>? watermarkHits = null)
        => new(
            Verdict: isUpgrade ? PackInstallVerdict.WouldUpgrade : PackInstallVerdict.WouldInstall,
            PackKey: PackKey,
            Version: version,
            SignerKeyId: null,
            Epoch: null,
            VouchingScope: TrustScope.HarborlineChannel,
            IsUpgrade: isUpgrade,
            PriorVersion: priorVersion,
            NewSeedKeys: Array.Empty<string>(),
            Conflicts: Array.Empty<Harborline.Api.Foundation.Packs.Install.Merge.PackReattachConflict>(),
            WatermarkHits: watermarkHits ?? Array.Empty<PackWatermarkHit>(),
            AdmissionRefusals: Array.Empty<Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal>(),
            RevocationStale: false,
            RefusalCodes: Array.Empty<string>(),
            CrossPackCollisions: Array.Empty<Harborline.Api.Foundation.Packs.Install.PackCrossPackCollision>(),
            UnmetContentReferences: Array.Empty<PackUnmetContentReference>());
}
