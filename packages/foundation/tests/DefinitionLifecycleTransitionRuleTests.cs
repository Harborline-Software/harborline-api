using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Tests;

/// <summary>
/// ck-10 S3b: the lifecycle transition rules every definition write's validate stage applies. The expected source
/// sets are literals from the lifecycle contract, never read back from the production collections.
/// </summary>
public sealed class DefinitionLifecycleTransitionRuleTests
{
    [Theory(DisplayName = "ck-10 S3b: each transition names its target status and exactly the source statuses it accepts")]
    [InlineData(DefinitionLifecycleTransition.Publish, DefinitionLifecycleStatus.Published,
        new[] { DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published })]
    [InlineData(DefinitionLifecycleTransition.Deprecate, DefinitionLifecycleStatus.Deprecated,
        new[] { DefinitionLifecycleStatus.Published, DefinitionLifecycleStatus.Deprecated })]
    [InlineData(DefinitionLifecycleTransition.Withdraw, DefinitionLifecycleStatus.Withdrawn,
        new[] { DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published, DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleStatus.Withdrawn })]
    [InlineData(DefinitionLifecycleTransition.Restore, DefinitionLifecycleStatus.Published,
        new[] { DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleStatus.Published })]
    public void Each_transition_has_its_contract_rule(
        DefinitionLifecycleTransition transition, DefinitionLifecycleStatus target, DefinitionLifecycleStatus[] allowedFrom)
    {
        var rule = ProbeLifecycle.Rule(transition);

        Assert.Equal(target, rule.Target);
        Assert.Equal(allowedFrom.Order(), rule.AllowedFrom.Order());
    }

    [Fact(DisplayName = "ck-10 S3b: an unknown transition is refused, never mapped to a default rule")]
    public void Unknown_transition_is_refused()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => ProbeLifecycle.Rule((DefinitionLifecycleTransition)99));

        Assert.Equal("transition", refused.ParamName);
        Assert.StartsWith("Unknown lifecycle transition.", refused.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "ck-10 S3b: an accepted source passes validate, reporting whether the status changes")]
    [InlineData(DefinitionLifecycleStatus.Draft, DefinitionLifecycleTransition.Publish, true)]
    [InlineData(DefinitionLifecycleStatus.Published, DefinitionLifecycleTransition.Publish, false)]
    [InlineData(DefinitionLifecycleStatus.Published, DefinitionLifecycleTransition.Deprecate, true)]
    [InlineData(DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleTransition.Deprecate, false)]
    [InlineData(DefinitionLifecycleStatus.Draft, DefinitionLifecycleTransition.Withdraw, true)]
    [InlineData(DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleTransition.Withdraw, false)]
    [InlineData(DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleTransition.Restore, true)]
    public void Accepted_source_passes(DefinitionLifecycleStatus current, DefinitionLifecycleTransition transition, bool changes) =>
        Assert.Equal(changes, new ProbeLifecycle().Require(new Probe(current), transition));

    [Theory(DisplayName = "ck-10 S3b: a source the transition does not accept is refused with the domain's failure")]
    [InlineData(DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleTransition.Publish)]
    [InlineData(DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleTransition.Publish)]
    [InlineData(DefinitionLifecycleStatus.Draft, DefinitionLifecycleTransition.Deprecate)]
    [InlineData(DefinitionLifecycleStatus.Draft, DefinitionLifecycleTransition.Restore)]
    [InlineData(DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleTransition.Restore)]
    public void Refused_source_is_the_domain_failure(DefinitionLifecycleStatus current, DefinitionLifecycleTransition transition)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new ProbeLifecycle().Require(new Probe(current), transition));

        Assert.Equal($"probe cannot transition from {current}", refused.Message);
    }

    private sealed record Probe(DefinitionLifecycleStatus Status);

    private sealed class ProbeLifecycle() : EntityStoreDefinitionLifecycle<Probe>(
        Storage(), Storage(), TimeProvider.System, new SchemaId("probe-schema"), "probe", "probeId", "probe", "local")
    {
        private static InMemoryEntityStore Storage() => new(new InMemoryAssetStorage(), TimeProvider.System);

        internal static (DefinitionLifecycleStatus Target, IReadOnlyCollection<DefinitionLifecycleStatus> AllowedFrom) Rule(
            DefinitionLifecycleTransition transition) => RuleFor(transition);

        internal bool Require(Probe existing, DefinitionLifecycleTransition transition)
        {
            var (target, allowedFrom) = RuleFor(transition);
            return RequireAllowedTransition(existing, target, allowedFrom);
        }

        protected override DefinitionCoordinates CoordinatesOf(Probe definition) => throw new NotSupportedException();

        protected override DefinitionLifecycleStatus StatusOf(Probe definition) => definition.Status;

        protected override Probe WithStatus(Probe definition, DefinitionLifecycleStatus status, DateTimeOffset transitionedAt) =>
            definition with { Status = status };

        protected override JsonDocument Serialize(Probe definition) => throw new NotSupportedException();

        protected override Probe Deserialize(JsonDocument body) => throw new NotSupportedException();

        protected override ActorId TransitionActor(Probe definition) => throw new NotSupportedException();

        protected override Exception CreateNotFoundException(DefinitionCoordinates coordinates) => new KeyNotFoundException();

        protected override Exception CreateInvalidTransitionException(
            Probe definition, DefinitionLifecycleStatus target, IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom) =>
            new InvalidOperationException($"probe cannot transition from {definition.Status}");

        protected override ValueTask ValidatePackRestoreAsync(Probe definition, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
