using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;

using LegalHoldRegistryAuthority = Harborline.Api.Foundation.Recovery.LegalHold.ILegalHoldRegistry;

namespace Harborline.Api.Foundation.Governance.Definitions;

/// <summary>Resolves a form definition's retention and legal-hold projection from their registries.</summary>
public interface IFormDefinitionEnvelopeResolver
{
    /// <summary>
    /// Resolves the policy governing records authored from <paramref name="definition"/> without
    /// rewriting the stored definition.
    /// </summary>
    ValueTask<ResolvedDefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>> ResolveAsync(
        FormDefinition definition,
        DateTimeOffset recordCreatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>A definition envelope projected from the current retention and legal-hold registries.</summary>
public sealed class ResolvedDefinitionEnvelope<TIdentity, TVersion, TTenant, TProvenance>
{
    internal ResolvedDefinitionEnvelope(
        DefinitionEnvelope<TIdentity, TVersion, TTenant, TProvenance> source,
        RetentionVerdict retentionClass,
        DefinitionLegalHold legalHold)
    {
        Source = source;
        RetentionClass = retentionClass;
        LegalHold = legalHold;
    }

    /// <summary>The authored and transported definition coordinates.</summary>
    public DefinitionEnvelope<TIdentity, TVersion, TTenant, TProvenance> Source { get; }

    /// <summary>The strongest current retention verdict governing records produced by the definition.</summary>
    public RetentionVerdict RetentionClass { get; }

    /// <summary>The current registry-derived hold state across the definition's governed record classes.</summary>
    public DefinitionLegalHold LegalHold { get; }
}

/// <summary>
/// Resolves form-envelope policy by composing the form's record classes with the existing retention
/// and legal-hold registries from legacy ADRs 0137, 0139, and 0142.
/// </summary>
public sealed class FormDefinitionEnvelopeResolver : IFormDefinitionEnvelopeResolver
{
    private readonly IAspectResolver _aspects;
    private readonly IFieldClassAuditEventClassMap _classMap;
    private readonly IRetentionPolicyResolver _retention;
    private readonly LegalHoldRegistryAuthority _legalHolds;

    /// <summary>Constructs the resolver over the existing registry authorities.</summary>
    public FormDefinitionEnvelopeResolver(
        IAspectResolver aspects,
        IFieldClassAuditEventClassMap classMap,
        IRetentionPolicyResolver retention,
        LegalHoldRegistryAuthority legalHolds)
    {
        _aspects = aspects ?? throw new ArgumentNullException(nameof(aspects));
        _classMap = classMap ?? throw new ArgumentNullException(nameof(classMap));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _legalHolds = legalHolds ?? throw new ArgumentNullException(nameof(legalHolds));
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedDefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>> ResolveAsync(
        FormDefinition definition,
        DateTimeOffset recordCreatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var resolvedAspects = definition.Overlay.Fields.Keys
            .Select(field => _aspects.Resolve(definition, field))
            .ToArray();
        var governingClasses = resolvedAspects
            .Select(aspect => aspect.Retention?.FloorClass)
            .Where(static floorClass => !string.IsNullOrWhiteSpace(floorClass))
            .Select(floorClass => _classMap.Resolve(floorClass!))
            .Distinct()
            .ToArray();

        if (governingClasses.Length == 0)
        {
            governingClasses = [AuditEventClass.Configuration];
        }

        var verdicts = new List<RetentionVerdict>(governingClasses.Length);
        foreach (var governingClass in governingClasses)
        {
            verdicts.Add(await _retention.ResolveAsync(
                definition.Tenant,
                governingClass,
                recordCreatedAt,
                cancellationToken).ConfigureAwait(false));
        }

        var held = false;
        var heldClasses = resolvedAspects
            .SelectMany(static aspect => aspect.Tags)
            .Select(static tag => tag.Code)
            .Concat(governingClasses.Select(static governingClass => governingClass.ToString()))
            .Distinct(StringComparer.Ordinal);
        foreach (var heldClass in heldClasses)
        {
            held |= await _legalHolds.IsHeldAsync(
                definition.Tenant,
                HeldRef.ForClass(heldClass),
                cancellationToken).ConfigureAwait(false);
        }

        var strongest = verdicts
            .OrderByDescending(static verdict => verdict.MinimumHoldUntil)
            .ThenByDescending(static verdict => verdict.MaximumHoldUntil)
            .ThenBy(static verdict => verdict.EventClass)
            .First();

        return new ResolvedDefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
            definition.Envelope,
            strongest,
            held ? DefinitionLegalHold.Held : DefinitionLegalHold.NotHeld);
    }
}
