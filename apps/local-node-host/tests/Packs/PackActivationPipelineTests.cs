using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

using static Harborline.Api.Kernel.Runtime.WritePipelineStage;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// ck-10 S5b (DES-0029): pack activation, deactivation and narrowing each run the six ADR 0038 stages
/// through <see cref="WritePipeline.RunAsync"/>. Every case asserts the stages the real installer entered
/// and the store's state, not only the returned outcome.
/// </summary>
public sealed class PackActivationPipelineTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-0000000005b5");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.s5b";
    private const string ContentKey = "s5b-item";
    private const string Seed = """{"label":"reviewed","note":"publisher"}""";

    private readonly InMemoryPackInstallStore _store = new();
    private readonly InMemoryPackInstallAudit _audit = new();
    private readonly StageRecorder _stages = new();
    private bool _allow = true;
    private readonly PackInstaller _installer;

    public PackActivationPipelineTests()
    {
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), _store,
            new WorkflowRefusingPackContentAdmission(), _audit,
            TestAuthorization.Gate(_ => _allow), pipelineObserver: _stages);
        SeedPack();
    }

    [Fact]
    public async Task An_allowed_activation_runs_the_six_stages_and_makes_the_version_active()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);

        var outcome = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Activated, outcome.Error + " " + outcome.Detail);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Activated && entry.PackKey == PackKey);
    }

    [Fact]
    public async Task A_refused_activation_decision_stops_at_authorize_and_nothing_becomes_active()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        _allow = false;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => _installer.ActivateAsync(Context(), PackKey, "1.0.0"));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.PreDecision && entry.Detail == PackInstallCodes.RefusedAuthorizationDenied);
    }

    [Fact]
    public async Task A_validate_refusal_stops_at_validate_with_nothing_active_and_a_retry_activates_once_the_requirement_is_met()
    {
        var refused = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(refused.Activated);
        Assert.Equal(PackInstallCodes.ActivatePlatformPackRequired, refused.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == PackKey && entry.Detail!.StartsWith(PackInstallCodes.ActivatePlatformPackRequired, StringComparison.Ordinal));

        // Recovery: the platform pack becomes active, and the same activation now commits.
        PlatformPackTestPreload.Activate(_store, Tenant);
        _stages.Entered.Clear();
        var retried = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(retried.Activated, retried.Error + " " + retried.Detail);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task Deactivation_runs_the_six_stages_and_leaves_the_pack_inactive()
    {
        await ActivateAsync();

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Deactivated, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Deactivated && entry.PackKey == PackKey);
    }

    [Fact]
    public async Task A_refused_deactivation_decision_stops_at_authorize_and_leaves_the_pack_active()
    {
        await ActivateAsync();
        _allow = false;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => _installer.DeactivateAsync(Context(), PackKey, "1.0.0"));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task A_deactivation_refused_at_validate_leaves_the_platform_pack_active()
    {
        await ActivateAsync();

        var outcome = await _installer.DeactivateAsync(Context(), PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version);

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Equal(PlatformPackTestPreload.Version, _store.GetActive(Tenant, PlatformPackTestPreload.PackKey)?.Version);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task Narrowing_runs_the_six_stages_and_stores_the_override()
    {
        await ActivateAsync();

        var outcome = await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, Decision());

        Assert.True(outcome.Recorded, outcome.RefusalCode);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        var stored = Assert.Single(_store.GetOverrides(Tenant, PackKey));
        Assert.Equal(ContentKey, stored.ContentKey);
        Assert.Equal("""{"note":null}""", stored.OverlayPatch.ToJsonString());
    }

    [Fact]
    public async Task A_widening_narrowing_is_refused_at_validate_with_the_override_unchanged()
    {
        await ActivateAsync();
        Assert.True((await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, Decision())).Recorded);
        _stages.Entered.Clear();

        var outcome = await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"extra":"added"}""")!, Decision());

        Assert.False(outcome.Recorded);
        Assert.Equal(PackTenantNarrowing.WideningRefusedCode, outcome.RefusalCode);
        Assert.Equal("/extra", outcome.WideningPath);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        var stored = Assert.Single(_store.GetOverrides(Tenant, PackKey));
        Assert.Equal("""{"note":null}""", stored.OverlayPatch.ToJsonString());
    }

    [Fact]
    public async Task A_refused_narrowing_decision_stops_at_authorize_with_no_override()
    {
        await ActivateAsync();
        var denied = await TestAuthorization.Gate(false).DecideAsync(
            new AuthorizationWriteContext(new ActorId("test-operator"), Tenant, Now)
                .Request(AuthorizationOperation.Parse(Permission.PackagesOperate), "pack", PackKey));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, denied));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    private async Task ActivateAsync()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        _stages.Entered.Clear();
    }

    private static AuthorizationDecision Decision() =>
        TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now);

    private static PackInstallContext Context() => new(
        Tenant, new InMemoryPackTrustStore([]), PackRevocationList.Empty, Now, TimeSpan.FromHours(1),
        Principal: "test-operator");

    private void SeedPack()
    {
        var item = new PackSeedItem(ContentKey, PackContentKind.FormDefinition, "1.0.0", Seed,
            Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(Seed)));
        _store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(PackKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, [item],
                new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                TrustScope.OwnRoster, [new PackDependencyRef(PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version)]),
            new PackInstallWatermark(PackKey, "1.0.0", new Dictionary<string, int>()), []));
    }

    private sealed class StageRecorder : IWritePipelineObserver
    {
        public List<WritePipelineStage> Entered { get; } = [];

        public void OnStage(WritePipelineStage stage) => Entered.Add(stage);
    }
}
