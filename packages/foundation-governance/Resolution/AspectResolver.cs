using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Policy;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>
/// The SPINE-2 form→section→field aspect resolver (ADR 0140 D2 §2). Walks the grains
/// coarsest→finest applying the per-class resolution rule (classification monotonic-union,
/// access monotonic-narrowing, lifecycle strengthen-only, residency intersection,
/// presentation/discovery override), rejecting any relax-attempt or unsatisfiable
/// residency fail-closed at publish, then composes the resolved tags' policies into the
/// trigger-indexed effect map.
/// </summary>
public sealed class AspectResolver : IAspectResolver
{
    private readonly IPolicyRegistry _registry;

    /// <summary>Construct over the policy registry the composition reads tag bindings from.</summary>
    public AspectResolver(IPolicyRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public ResolvedAspect Resolve(FormDefinition def, string field, IReadOnlyList<FormDefinition>? ancestors = null)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (string.IsNullOrEmpty(field)) throw new ArgumentException("Field name required.", nameof(field));

        // Cross-definition lineage is EXTENDS, single-parent, depth <= 3 (def + <= 2 ancestors).
        if (ancestors is { Count: > 2 })
        {
            throw Fail(def, $"aspect.lineage_depth_exceeded: field '{field}' lineage exceeds depth 3.");
        }

        if (!def.Overlay.Fields.TryGetValue(field, out var fieldOverlay))
        {
            throw Fail(def, $"aspect.unknown_field: field '{field}' is not declared in the overlay.");
        }

        // Locate the field's section + its container chain (SPINE-2 item 6): a NESTED field
        // (inside a group/collection item tree) is NOT in FormSection.Fields, so the flat
        // lookup misses it — its enclosing section + each container between the section and the
        // field are additional resolver grains (coarsest → finest).
        var (section, containerChain) = LocateField(def, field);
        var grains = BuildGrains(def, ancestors, section, containerChain, fieldOverlay);

        var tags = WalkClassification(def, field, grains, fieldOverlay.PiiSensitivity);
        var (read, write, conditions) = WalkAccess(def, field, grains);
        var retention = WalkRetention(def, field, grains);
        var residency = WalkResidency(def, field, grains);
        var immutability = WalkImmutability(def, field, grains);
        var provenance = grains.Select(g => g.Lifecycle?.Provenance).LastOrDefault(p => p is not null);
        var discovery = grains.Select(g => g.Discovery).LastOrDefault(d => d is not null);

        return new ResolvedAspect(field, tags, read, write, conditions,
            retention, residency, immutability, provenance, discovery);
    }

    /// <inheritdoc />
    public ResolvedFieldPolicy ResolvePolicy(
        FormDefinition def,
        string field,
        IReadOnlyList<FormDefinition>? ancestors = null,
        IReadOnlyList<string>? regimePrecedence = null)
    {
        var aspect = Resolve(def, field, ancestors);

        var raw = new List<PolicyEffect>();
        foreach (var tag in aspect.Tags)
        {
            var binding = _registry.Resolve(tag);
            if (binding is not null) raw.AddRange(binding.Effects);
        }

        var byTrigger = new Dictionary<Trigger, IReadOnlyList<PolicyEffect>>();
        foreach (Trigger trigger in Enum.GetValues<Trigger>())
        {
            var atTrigger = raw.Where(e => e.Triggers.Contains(trigger)).ToList();
            if (atTrigger.Count == 0) continue;
            byTrigger[trigger] = Dedup(def, field, trigger, atTrigger, regimePrecedence);
        }

        return new ResolvedFieldPolicy(field, aspect.Tags, byTrigger, aspect);
    }

    // ── Grain assembly ──────────────────────────────────────────────────────────

    private sealed record Grain(
        IReadOnlyList<Tag>? Tags,
        IReadOnlyList<string>? ReadRoles,
        IReadOnlyList<string>? WriteRoles,
        string? ReadCondition,
        RetentionRequirement? Retention,
        ResidencyRequirement? Residency,
        Immutability? Immutability,
        Provenance? Provenance,
        DiscoveryAspect? Discovery)
    {
        public LifecycleAspect? Lifecycle =>
            Retention is null && Residency is null && Immutability is null && Provenance is null
                ? null
                : new LifecycleAspect(Retention, Residency, Immutability ?? Forms.Models.Immutability.Mutable, Provenance);
    }

    private static List<Grain> BuildGrains(
        FormDefinition def, IReadOnlyList<FormDefinition>? ancestors, FormSection? section,
        IReadOnlyList<FormItem> containerChain, FieldOverlay fieldOverlay)
    {
        var grains = new List<Grain>();

        // Cross-definition lineage: ancestor form-grain aspects are the coarsest (coarsest-first).
        if (ancestors is not null)
        {
            foreach (var ancestor in ancestors)
            {
                grains.Add(FromAspectOverlay(ancestor.Overlay.Aspects, readRoles: null, writeRoles: null, readCondition: null));
            }
        }

        // Form grain.
        grains.Add(FromAspectOverlay(def.Overlay.Aspects, readRoles: null, writeRoles: null, readCondition: null));

        // Section grain — SectionAccess roles + the section's optional aspect overlay.
        if (section is not null)
        {
            var sectionAspectRead = section.Aspects?.Access?.ReadRoles;
            var sectionAspectWrite = section.Aspects?.Access?.WriteRoles;
            grains.Add(FromAspectOverlay(
                section.Aspects,
                readRoles: Combine(section.Access.ReadRoles, sectionAspectRead),
                writeRoles: Combine(section.Access.WriteRoles, sectionAspectWrite),
                readCondition: section.Access.ReadConditionExpression));
        }

        // Container grains (SPINE-2 item 6) — each group/collection between the section and the
        // field, coarsest (outermost) → finest, so a container tag flows to its children under the
        // same monotonic-union rule as section→field. Access narrows through the container aspect.
        foreach (var container in containerChain)
        {
            grains.Add(FromAspectOverlay(
                container.Aspects,
                readRoles: container.Aspects?.Access?.ReadRoles,
                writeRoles: container.Aspects?.Access?.WriteRoles,
                readCondition: container.Aspects?.Access?.ReadConditionExpression));
        }

        // Field grain — the field's aspect overlay + the legacy field-role overrides.
        var fieldRead = Combine(fieldOverlay.FieldReadRoles, fieldOverlay.Aspects?.Access?.ReadRoles);
        var fieldWrite = Combine(fieldOverlay.FieldWriteRoles, fieldOverlay.Aspects?.Access?.WriteRoles);
        grains.Add(FromAspectOverlay(
            fieldOverlay.Aspects,
            readRoles: fieldRead,
            writeRoles: fieldWrite,
            readCondition: fieldOverlay.Aspects?.Access?.ReadConditionExpression));

        return grains;
    }

    /// <summary>
    /// Locate a field's enclosing section and the container chain (outermost → innermost) from
    /// that section down to the field. A top-level field returns its section + an empty chain; a
    /// nested field (inside the section's <see cref="FormSection.Items"/> tree) returns the section
    /// + every <c>group</c>/<c>collection</c> ancestor. A field present only in the flat
    /// <see cref="FormSection.Fields"/> of no section (packet-authored orphan) returns (null, []).
    /// </summary>
    private static (FormSection? Section, IReadOnlyList<FormItem> Chain) LocateField(FormDefinition def, string field)
    {
        foreach (var section in def.Overlay.Sections)
        {
            // Top-level (flat) field of this section — no container chain.
            if (section.Fields.Contains(field))
            {
                return (section, Array.Empty<FormItem>());
            }

            // Nested field — walk the item tree tracking the container ancestors.
            var chain = new List<FormItem>();
            if (section.Items is { Count: > 0 } && FindInItems(section.Items, field, chain))
            {
                return (section, chain);
            }
        }

        return (null, Array.Empty<FormItem>());
    }

    /// <summary>Depth-first search for a <c>field</c> item with key <paramref name="field"/>,
    /// pushing each container ancestor onto <paramref name="chain"/> (outermost first) on the way
    /// down and popping it on the way back up.</summary>
    private static bool FindInItems(IReadOnlyList<FormItem> items, string field, List<FormItem> chain)
    {
        foreach (var item in items)
        {
            if (item.Kind == FormItemKind.Field)
            {
                if (string.Equals(item.Key, field, StringComparison.Ordinal)) return true;
                continue;
            }
            if (item.Kind is FormItemKind.Group or FormItemKind.Collection && item.Items is { Count: > 0 })
            {
                chain.Add(item);
                if (FindInItems(item.Items, field, chain)) return true;
                chain.RemoveAt(chain.Count - 1);
            }
        }
        return false;
    }

    private static Grain FromAspectOverlay(
        AspectOverlay? overlay,
        IReadOnlyList<string>? readRoles,
        IReadOnlyList<string>? writeRoles,
        string? readCondition)
        => new(
            Tags: overlay?.Classification?.Tags,
            ReadRoles: readRoles,
            WriteRoles: writeRoles,
            ReadCondition: readCondition,
            Retention: overlay?.Lifecycle?.Retention,
            Residency: overlay?.Lifecycle?.Residency,
            Immutability: overlay?.Lifecycle is { } lc ? lc.Immutability : null,
            Provenance: overlay?.Lifecycle?.Provenance,
            Discovery: overlay?.Discovery);

    // ── Per-axis walks ──────────────────────────────────────────────────────────

    private static IReadOnlyList<Tag> WalkClassification(
        FormDefinition def, string field, List<Grain> grains, PiiSensitivity pii)
    {
        IReadOnlyList<Tag> inherited = Array.Empty<Tag>();
        foreach (var g in grains)
        {
            if (g.Tags is null) continue; // pure inheritance at this grain
            var declaredKeys = g.Tags.Select(InMemoryPolicyRegistry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var t in inherited)
            {
                if (!declaredKeys.Contains(InMemoryPolicyRegistry.Key(t)))
                {
                    throw Fail(def,
                        $"aspect.relax_forbidden: field '{field}' attempts to clear inherited tag '{t.Code}'.");
                }
            }
            inherited = g.Tags;
        }

        // PiiSensitivity.Sensitive ⇒ the predefined 'pii' tag (back-compat sugar, §3.2),
        // added additively at the finest grain so it can never trip the relax check.
        if (pii == PiiSensitivity.Sensitive)
        {
            var key = InMemoryPolicyRegistry.Key(PredefinedPolicyBindings.Pii);
            if (!inherited.Any(t => InMemoryPolicyRegistry.Key(t) == key))
            {
                inherited = inherited.Append(PredefinedPolicyBindings.Pii).ToList();
            }
        }
        return inherited;
    }

    private static (IReadOnlyList<string>? Read, IReadOnlyList<string>? Write, IReadOnlyList<string> Conditions)
        WalkAccess(FormDefinition def, string field, List<Grain> grains)
    {
        IReadOnlyList<string>? read = null;
        IReadOnlyList<string>? write = null;
        var conditions = new List<string>();

        foreach (var g in grains)
        {
            read = NarrowRoles(def, field, "read", read, g.ReadRoles);
            write = NarrowRoles(def, field, "write", write, g.WriteRoles);
            if (!string.IsNullOrEmpty(g.ReadCondition)) conditions.Add(g.ReadCondition!);
        }
        return (read, write, conditions);
    }

    private static IReadOnlyList<string>? NarrowRoles(
        FormDefinition def, string field, string kind,
        IReadOnlyList<string>? inherited, IReadOnlyList<string>? declared)
    {
        if (declared is null) return inherited;        // grain doesn't constrain ⇒ inherit
        if (inherited is null) return declared;        // first constraint sets the ceiling
        var inheritedSet = inherited.ToHashSet(StringComparer.Ordinal);
        foreach (var r in declared)
        {
            if (!inheritedSet.Contains(r))
            {
                throw Fail(def,
                    $"aspect.relax_forbidden: field '{field}' attempts to widen inherited {kind} role with '{r}'.");
            }
        }
        return declared; // ⊆ inherited ⇒ the narrowed (intersection) set
    }

    private static RetentionRequirement? WalkRetention(FormDefinition def, string field, List<Grain> grains)
    {
        RetentionRequirement? effective = null;
        foreach (var g in grains)
        {
            var r = g.Retention;
            if (r is null) continue;
            if (effective is not null && r.MinimumRetentionDays < effective.MinimumRetentionDays)
            {
                throw Fail(def,
                    $"aspect.relax_forbidden: field '{field}' attempts to shorten inherited retention " +
                    $"({effective.MinimumRetentionDays}d → {r.MinimumRetentionDays}d).");
            }
            effective = effective is null || r.MinimumRetentionDays >= effective.MinimumRetentionDays ? r : effective;
        }
        return effective;
    }

    private static ResidencyRequirement? WalkResidency(FormDefinition def, string field, List<Grain> grains)
    {
        HashSet<string>? allowed = null;
        var prohibited = new HashSet<string>(StringComparer.Ordinal);
        var constrained = false;

        foreach (var g in grains)
        {
            var r = g.Residency;
            if (r is null) continue;
            constrained = true;
            if (r.ProhibitedJurisdictions is not null)
            {
                foreach (var p in r.ProhibitedJurisdictions) prohibited.Add(p);
            }
            var declared = r.AllowedJurisdictions.ToHashSet(StringComparer.Ordinal);
            allowed = allowed is null ? declared : allowed.Intersect(declared).ToHashSet(StringComparer.Ordinal);
            if (allowed.Count == 0)
            {
                throw Fail(def,
                    $"aspect.residency_unsatisfiable: field '{field}' residency intersection is empty.");
            }
        }

        if (!constrained) return null;
        return new ResidencyRequirement(
            allowed?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            prohibited.Count == 0 ? null : prohibited.ToList());
    }

    private static Immutability WalkImmutability(FormDefinition def, string field, List<Grain> grains)
    {
        Immutability effective = Immutability.Mutable;
        var seen = false;
        foreach (var g in grains)
        {
            if (g.Immutability is not { } gi) continue;
            if (seen && gi < effective)
            {
                throw Fail(def,
                    $"aspect.relax_forbidden: field '{field}' attempts to lower inherited immutability " +
                    $"({effective} → {gi}).");
            }
            effective = (Immutability)Math.Max((int)effective, (int)gi);
            seen = true;
        }
        return effective;
    }

    // ── Composition ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<PolicyEffect> Dedup(
        FormDefinition def, string field, Trigger trigger,
        List<PolicyEffect> atTrigger, IReadOnlyList<string>? regimePrecedence)
    {
        var result = new List<PolicyEffect>();
        foreach (var kind in atTrigger.Select(e => e.Kind).Distinct())
        {
            var group = atTrigger.Where(e => e.Kind == kind).ToList();
            switch (kind)
            {
                case EffectKind.Encrypt:
                    result.Add(new PolicyEffect(EffectKind.Encrypt, new[] { trigger },
                        new EffectParams(SubjectScoped: group.Any(g => g.Params.SubjectScoped))));
                    break;
                case EffectKind.Mask:
                    // Most-restrictive reveal: null (reveal none) beats any number; otherwise the smallest.
                    int? reveal = group.Any(g => g.Params.MaskRevealLast is null)
                        ? null
                        : group.Min(g => g.Params.MaskRevealLast);
                    result.Add(new PolicyEffect(EffectKind.Mask, new[] { trigger },
                        new EffectParams(MaskRevealLast: reveal)));
                    break;
                case EffectKind.Retain:
                    result.Add(ResolveRetain(def, field, trigger, group, regimePrecedence));
                    break;
                default: // Redact, Audit, Reside, Consent — single effect (params from the first).
                    result.Add(new PolicyEffect(kind, new[] { trigger }, group[0].Params));
                    break;
            }
        }
        return result;
    }

    private static PolicyEffect ResolveRetain(
        FormDefinition def, string field, Trigger trigger,
        List<PolicyEffect> group, IReadOnlyList<string>? regimePrecedence)
    {
        var regimes = group
            .Select(g => g.Params.RetainRegime)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (regimes.Count <= 1)
        {
            // No regime conflict — the longest floor wins (floor-wins, ADR 0137).
            var longest = group.OrderByDescending(g => g.Params.RetainMinimumDays ?? 0).First();
            return new PolicyEffect(EffectKind.Retain, new[] { trigger }, longest.Params);
        }

        // Conflicting regimes — must be reconciled by an explicit precedence, never silently.
        if (regimePrecedence is null || regimes.Any(r => !regimePrecedence.Contains(r!, StringComparer.Ordinal)))
        {
            throw Fail(def,
                $"aspect.regime_conflict: field '{field}' has conflicting retention regimes " +
                $"[{string.Join(", ", regimes)}]; declare regimePrecedence to resolve.");
        }

        var winner = group
            .OrderBy(g => regimePrecedence.ToList().IndexOf(g.Params.RetainRegime!))
            .First();
        return new PolicyEffect(EffectKind.Retain, new[] { trigger }, winner.Params);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string>? Combine(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Intersect(b, StringComparer.Ordinal).ToList();
    }

    private static FormDefinitionValidationException Fail(FormDefinition def, string message)
        => new(def.Id, message);
}
