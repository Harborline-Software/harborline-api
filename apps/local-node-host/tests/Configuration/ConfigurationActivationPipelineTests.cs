using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>ck-10 S5a: configuration activation uses the real executor over the durable SQLCipher host store.</summary>
public sealed class ConfigurationActivationPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Frozen = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-configuration-pipeline");

    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationActivationTarget _target = null!;
    private Observer _observer = null!;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 119);
        _store = new DurablePackInstallStore(_db.Factory);
        _observer = new Observer();
        _target = Target(TestPackGate.AllowAll());
        Seed("acme.core", ["form.shared", "form.core"]);
        Seed("acme.ext", ["form.shared", "form.ext"]);
        _store.RecordKeyOwnership(Tenant, "form.shared", "acme.core");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Allowed_activation_runs_the_six_literal_stages_and_moves_the_effective_pointer()
    {
        var (request, candidate) = Prepare(_target, "intent-allowed");

        var outcome = await ActivateAsync(_target, request);

        Assert.Null(outcome.Decision.Refusal);
        Assert.Equal(
            [WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
             WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React],
            _observer.Stages);
        Assert.Equal(candidate, _target.ReadEffective(Tenant).Digest);
    }

    [Fact]
    public async Task Refused_decision_stops_at_authorize_without_writing_pointer_or_evidence()
    {
        var (request, candidate) = Prepare(_target, "intent-refused");
        var denying = Target(TestPackGate.Denying());
        var prior = denying.ReadEffective(Tenant).Digest;

        var outcome = await ActivateAsync(denying, request);

        Assert.Equal("configuration-authority-refused", outcome.Decision.Refusal?.Code);
        Assert.False(outcome.Decision.Authority!.Allowed);
        Assert.Equal([WritePipelineStage.Authorize], _observer.Stages);
        Assert.Equal(prior, denying.ReadEffective(Tenant).Digest);
        Assert.NotEqual(candidate, denying.ReadEffective(Tenant).Digest);
        Assert.Empty(Outbox());
    }

    [Fact]
    public async Task Reused_evidence_intent_settles_at_bind_without_writing_again()
    {
        var (request, candidate) = Prepare(_target, "intent-reused");
        await ActivateAsync(_target, request);
        _observer.Stages.Clear();
        var outboxBefore = Outbox();

        var outcome = await ActivateAsync(_target, request);

        Assert.Equal("configuration-evidence-intent-acknowledged", outcome.Decision.Refusal?.Code);
        Assert.Equal([WritePipelineStage.Authorize, WritePipelineStage.Bind], _observer.Stages);
        Assert.Equal(candidate, _target.ReadEffective(Tenant).Digest);
        Assert.Single(outboxBefore);
        Assert.Single(Outbox());
    }

    [Fact]
    public async Task Missing_prepared_projection_refuses_at_validate_without_writing()
    {
        var (request, _) = Prepare(_target, "intent-validate-refused");
        var prior = _target.ReadEffective(Tenant).Digest;
        using (var context = _db.CreateContext())
        {
            context.PreparedProjections.RemoveRange(context.PreparedProjections);
            context.SaveChanges();
        }

        var outcome = await ActivateAsync(_target, request);

        Assert.Equal("configuration-projection-missing", outcome.Decision.Refusal?.Code);
        Assert.Equal(
            [WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate, WritePipelineStage.Validate],
            _observer.Stages);
        Assert.Equal(prior, _target.ReadEffective(Tenant).Digest);
        Assert.Empty(Outbox());
    }

    [Fact]
    public async Task Fresh_intent_after_a_validate_refusal_recovers_and_activates()
    {
        var (refused, _) = Prepare(_target, "intent-recovery-refused");
        using (var context = _db.CreateContext())
        {
            context.PreparedProjections.RemoveRange(context.PreparedProjections);
            context.SaveChanges();
        }
        var refusal = await ActivateAsync(_target, refused);
        Assert.Equal("configuration-projection-missing", refusal.Decision.Refusal?.Code);
        _observer.Stages.Clear();

        var (retry, candidate) = Prepare(_target, "intent-recovery-fresh");
        var outcome = await ActivateAsync(_target, retry);

        Assert.Null(outcome.Decision.Refusal);
        Assert.Equal(
            [WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
             WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React],
            _observer.Stages);
        Assert.Equal(candidate, _target.ReadEffective(Tenant).Digest);
        Assert.Single(Outbox());
    }

    private ConfigurationActivationTarget Target(AuthorizationGate gate) =>
        new(_db.Factory, _store, gate, evidence: null, pipelineObserver: _observer);

    private (ConfigurationActivationRequest Request, string Candidate) Prepare(ConfigurationActivationTarget target, string intentId)
    {
        var baseline = target.ReadEffective(Tenant).Digest;
        var prepared = target.Prepare(Tenant, baseline, ["acme.core", "acme.ext"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["form.shared"] = "acme.ext" }, Frozen);
        var activation = prepared.Preparation!.Prepared!;
        return (new ConfigurationActivationRequest(activation, "test:operator",
            new ConfigurationEvidenceIntent(intentId, "T-519 configuration pipeline test")), activation.Candidate.Digest);
    }

    private static ValueTask<ConfigurationActivationOutcome> ActivateAsync(
        ConfigurationActivationTarget target,
        ConfigurationActivationRequest request) =>
        target.For(new AuthorizationWriteContext(new ActorId("test:operator"), Tenant, Frozen)).CompareAndSwapAsync(request);

    private List<ConfigurationEvidenceOutboxRow> Outbox()
    {
        using var context = _db.CreateContext();
        return context.EvidenceOutbox.AsNoTracking().ToList();
    }

    private void Seed(string packKey, string[] contentKeys)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(Tenant, pack,
            new PackInstallWatermark(packKey, "1.0.0", new Dictionary<string, int>()), []));
        _store.Activate(Tenant, packKey, "1.0.0");
    }

    private sealed class Observer : IWritePipelineObserver
    {
        public List<WritePipelineStage> Stages { get; } = [];
        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);
    }
}
