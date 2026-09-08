using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Admission;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

/// <summary>
/// Ticket 260 s21 — the data-classification system id is RENAMED by registering the new id
/// alongside the retired one, never by swapping the literal.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of the accept-both window is that the validator stays CLOSED on both sides of
/// it. A bare literal swap fails SOFT, not closed: the new id differs from the retired one in its
/// entire prefix, so it is far outside the near-miss edit-distance budget, and every already-frozen
/// definition tagged with the retired id would silently become an unbound open-vocab system — no
/// encryption, no redaction, no audit on PII/PHI fields (the retro #1685 F1 asymmetry). These tests
/// pin all four axes at once: both accepted ids bind and admit, a near miss of EITHER is rejected,
/// an unknown kind in EITHER is rejected, and a genuinely-distinct custom system still admits.
/// </para>
/// </remarks>
public sealed class DataClassificationSystemRenameTests
{
    private const string Retired = PredefinedPolicyBindings.DataClassificationSystem;
    private const string Renamed = PredefinedPolicyBindings.DataClassificationSystemRenamed;

    [Theory(DisplayName = "260 s21: a pii tag in EITHER accepted data-classification system admits and binds")]
    [InlineData(Retired)]
    [InlineData(Renamed)]
    public void Both_Accepted_Systems_Bind_And_Admit(string system)
    {
        var registry = new InMemoryPolicyRegistry();
        var tag = new Tag(system, "pii");

        var binding = registry.Resolve(tag);

        Assert.NotNull(binding);
        // Same binding object as the retired spelling resolves to — one policy, two names, so the
        // renamed id inherits Encrypt@Store / Redact@Read / Audit rather than a parallel copy that
        // could drift.
        Assert.Same(registry.Resolve(new Tag(Retired, "pii")), binding);

        Validator().ValidateAtPublish(Definition(tag)); // does not throw
    }

    [Theory(DisplayName = "260 s21: a near miss of EITHER accepted system is rejected fail-closed")]
    [InlineData("shipyard/data-classificaton")] // one-character slip on the retired id
    [InlineData("harborline/data-classificaton")] // the same slip on the renamed id
    [InlineData("Harborline/data-classification")] // case variant of the renamed id
    [InlineData("SHIPYARD/data-classification")] // case variant of the retired id
    public void Near_Miss_Of_Either_System_Is_Rejected(string system)
    {
        var error = Assert.Throws<FormDefinitionValidationException>(
            () => Validator().ValidateAtPublish(Definition(new Tag(system, "pii"))));

        Assert.Contains("aspect.suspect_system", error.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "260 s21: an unknown kind in EITHER accepted system is rejected fail-closed")]
    [InlineData(Retired)]
    [InlineData(Renamed)]
    public void Unknown_Kind_In_Either_System_Is_Rejected(string system)
    {
        var error = Assert.Throws<FormDefinitionValidationException>(
            () => Validator().ValidateAtPublish(Definition(new Tag(system, "pii-typo"))));

        Assert.Contains("aspect.unknown_kind", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "260 s21 control: a genuinely-distinct custom system still admits as open-vocab")]
    public void Distinct_Custom_System_Still_Admits()
    {
        // Far outside the near-miss budget in both directions and not a class-required code:
        // widening the accepted set must not widen what counts as a near miss of it.
        Validator().ValidateAtPublish(Definition(new Tag("acme/labels", "internal")));
    }

    [Fact(DisplayName = "260 s21 control: a whitespace-dirty accepted system still fails closed on binding")]
    public void Whitespace_Dirty_Renamed_System_Fails_Closed()
    {
        // The validator trims for the predefined/near-miss decision, but binding resolution keys off
        // the RAW tag — so a stored tag that cannot resolve at runtime is refused (2b), never
        // admitted as protected on the strength of a trim. That posture must survive the rename.
        var error = Assert.Throws<FormDefinitionValidationException>(
            () => Validator().ValidateAtPublish(Definition(new Tag(Renamed + " ", "pii"))));

        Assert.Contains("aspect.unclassified_required", error.Message, StringComparison.Ordinal);
    }

    private static PolicyAdmissionValidator Validator()
    {
        var registry = new InMemoryPolicyRegistry();
        return new PolicyAdmissionValidator(new AspectResolver(registry), registry);
    }

    private static FormDefinition Definition(Tag tag) => new(
        Id: new FormDefinitionId("forms.classification-rename"),
        Version: new SemanticVersion(1, 0, 0),
        Status: FormDefinitionStatus.Draft,
        Tenant: new TenantId("aaaaaaaa-0000-0000-0000-000000000260"),
        Owner: IdentityRef.System,
        SchemaRef: new SchemaId("schemas.classification-rename"),
        Overlay: new HarborlineOverlay(
            Fields: new Dictionary<string, FieldOverlay>
            {
                ["subject_name"] = new(
                    InternationalizedText.FromInvariant("Subject name"),
                    Aspects: new AspectOverlay(Classification: new ClassificationAspect(new[] { tag }))),
            },
            Sections: Array.Empty<FormSection>(),
            Rules: Array.Empty<RuleDefinition>()),
        Lineage: null,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
