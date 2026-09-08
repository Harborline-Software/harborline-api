using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// The D4 reuse-cascade resolver (ADR 0135/0140 amendments 2026-07-01) over
/// <see cref="IReusableUnitStore"/>. Expands <see cref="FormItemKind.Reference"/> nodes into
/// groups and merges referenced units' fields CP-locked into the effective overlay, emitting
/// ADR 0129 D8 provenance. Fail-closed on every resolution error — a definition that cannot be
/// resolved cleanly is never partially expanded.
/// </summary>
public sealed class ReuseResolver : IReuseResolver
{
    private readonly IReusableUnitStore _units;

    /// <summary>Constructs the resolver over the unit store references resolve against.</summary>
    public ReuseResolver(IReusableUnitStore units)
        => _units = units ?? throw new ArgumentNullException(nameof(units));

    /// <inheritdoc />
    public async ValueTask<ResolvedFormDefinition> ResolveAsync(FormDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Effective field map starts from the definition's OWN fields. A referenced unit's
        // fields are merged in CP-locked; a collision with an own key is an override attempt.
        var effectiveFields = new Dictionary<string, FieldOverlay>(definition.Overlay.Fields, StringComparer.Ordinal);
        var ownKeys = new HashSet<string>(definition.Overlay.Fields.Keys, StringComparer.Ordinal);
        var reusedKeys = new HashSet<string>(StringComparer.Ordinal);
        var provenance = new Dictionary<string, ReuseProvenance>(StringComparer.Ordinal);

        var rewrittenSections = new List<FormSection>(definition.Overlay.Sections.Count);
        foreach (var section in definition.Overlay.Sections)
        {
            if (section.Items is not { Count: > 0 })
            {
                rewrittenSections.Add(section);
                continue;
            }

            var rewritten = await RewriteItemsAsync(
                definition, section.Items, effectiveFields, ownKeys, reusedKeys, provenance, ct)
                .ConfigureAwait(false);
            rewrittenSections.Add(section with { Items = rewritten });
        }

        // No references anywhere ⇒ resolve to self (empty provenance). Cheap identity path.
        if (provenance.Count == 0)
        {
            return new ResolvedFormDefinition(definition, provenance);
        }

        var effective = definition with
        {
            Overlay = definition.Overlay with
            {
                Fields = effectiveFields,
                Sections = rewrittenSections,
            },
        };

        return new ResolvedFormDefinition(effective, provenance);
    }

    private async Task<IReadOnlyList<FormItem>> RewriteItemsAsync(
        FormDefinition definition,
        IReadOnlyList<FormItem> items,
        Dictionary<string, FieldOverlay> effectiveFields,
        HashSet<string> ownKeys,
        HashSet<string> reusedKeys,
        Dictionary<string, ReuseProvenance> provenance,
        CancellationToken ct)
    {
        var result = new List<FormItem>(items.Count);
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case FormItemKind.Reference:
                    result.Add(await ExpandReferenceAsync(
                        definition, item, effectiveFields, ownKeys, reusedKeys, provenance, ct)
                        .ConfigureAwait(false));
                    break;

                case FormItemKind.Group:
                case FormItemKind.Collection:
                    // Recurse — a reference may be nested inside a container in the consuming def.
                    var rewrittenChildren = await RewriteItemsAsync(
                        definition, item.Items!, effectiveFields, ownKeys, reusedKeys, provenance, ct)
                        .ConfigureAwait(false);
                    result.Add(item with { Items = rewrittenChildren });
                    break;

                default: // Field — unchanged.
                    result.Add(item);
                    break;
            }
        }

        return result;
    }

    private async Task<FormItem> ExpandReferenceAsync(
        FormDefinition definition,
        FormItem reference,
        Dictionary<string, FieldOverlay> effectiveFields,
        HashSet<string> ownKeys,
        HashSet<string> reusedKeys,
        Dictionary<string, ReuseProvenance> provenance,
        CancellationToken ct)
    {
        var unitRef = reference.Reference
            ?? throw new ReuseResolutionException(
                definition.Id, null, ReusableUnitCodes.UnresolvedReference,
                $"reference item '{reference.Key}' carries no reusable-unit reference.");

        var unit = await ResolveUnitAsync(definition, unitRef, ct).ConfigureAwait(false);

        if (unit.Kind != ReusableUnitKind.FormComponent || unit.Component is null)
        {
            throw new ReuseResolutionException(
                definition.Id, unit.Id, ReusableUnitCodes.WorkflowSubgraphUnsupported,
                $"reference '{reference.Key}' targets a {unit.Kind} unit; only FormComponent units can be referenced from a form in Phase 1.");
        }

        // Merge the unit's fields CP-locked. A collision with an OWN field is an override
        // attempt (rejected); a collision with another reused field is a duplicate.
        foreach (var (fieldKey, overlay) in unit.Component.Fields)
        {
            if (ownKeys.Contains(fieldKey))
            {
                throw new ReuseResolutionException(
                    definition.Id, unit.Id, ReusableUnitCodes.LockedFieldOverride,
                    $"definition declares field '{fieldKey}' which reusable unit '{unit.Id}' owns and CP-locks; a consumer cannot override a locked property in Phase 1.");
            }

            if (!reusedKeys.Add(fieldKey))
            {
                throw new ReuseResolutionException(
                    definition.Id, unit.Id, ReusableUnitCodes.DuplicateReusedField,
                    $"field '{fieldKey}' is contributed by more than one reference; the flat field registry cannot carry two overlays for one key (per-reference-site field namespacing is a Phase-1 follow-on).");
            }

            effectiveFields[fieldKey] = overlay;
            provenance[fieldKey] = new ReuseProvenance(unit.Id, unit.Version, reference.Key, ReuseLock.CpLocked);
        }

        // Replace the reference with a Group nesting the unit's resolved subtree under the
        // reference-site key. The unit body carries no nested references (validated at the
        // unit's registration), so its items splice in directly.
        return new FormItem(
            FormItemKind.Group,
            reference.Key,
            unit.Component.Items,
            Cardinality: null,
            Title: reference.Title ?? unit.Title);
    }

    private async Task<ReusableUnit> ResolveUnitAsync(FormDefinition definition, ReusableUnitRef unitRef, CancellationToken ct)
    {
        if (unitRef.Version.IsLatestPublished)
        {
            var published = await _units.GetCurrentPublishedAsync(definition.Tenant, unitRef.UnitId, ct).ConfigureAwait(false);
            return published ?? throw new ReuseResolutionException(
                definition.Id, unitRef.UnitId, ReusableUnitCodes.UnresolvedReference,
                $"no published version of reusable unit '{unitRef.UnitId}' exists in tenant '{definition.Tenant}'.");
        }

        var version = unitRef.Version.PinnedVersion!.Value;
        ReusableUnit pinned;
        try
        {
            pinned = await _units.GetAsync(definition.Tenant, unitRef.UnitId, version, ct).ConfigureAwait(false);
        }
        catch (ReusableUnitNotFoundException)
        {
            throw new ReuseResolutionException(
                definition.Id, unitRef.UnitId, ReusableUnitCodes.UnresolvedReference,
                $"reusable unit '{unitRef.UnitId}' at pinned version '{version}' does not exist in tenant '{definition.Tenant}'.");
        }

        // A pinned reference resolves only a renderable revision — a Draft is authoring scratch
        // and a Withdrawn revision rejects rendering (fail-closed, mirrors FormDefinitionStatus).
        if (pinned.Status is not (FormDefinitionStatus.Published or FormDefinitionStatus.Deprecated))
        {
            throw new ReuseResolutionException(
                definition.Id, unitRef.UnitId, ReusableUnitCodes.UnresolvedReference,
                $"reusable unit '{unitRef.UnitId}' at pinned version '{version}' is {pinned.Status} (not renderable).");
        }

        return pinned;
    }
}
