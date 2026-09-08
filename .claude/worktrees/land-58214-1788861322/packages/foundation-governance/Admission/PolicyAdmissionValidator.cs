using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.Foundation.Governance.Admission;

/// <summary>
/// Fail-closed publish-time admission validator (ADR 0140 D2 §3.4). A structural walk over
/// the resolved aspect graph (every field × its resolved tags) that throws
/// <see cref="FormDefinitionValidationException"/> on the first authoring violation — the
/// A1 throw-at-load shape (mirrors <c>WorkflowDefinitionBuilder.ValidateReachability</c> /
/// Harborline App <c>authorityOf</c>): a definition either admits whole or not at all.
/// </summary>
public sealed class PolicyAdmissionValidator : IPolicyAdmissionValidator
{
    private readonly IAspectResolver _resolver;
    private readonly IPolicyRegistry _registry;
    private readonly IReadOnlyCollection<string> _requiredClasses;
    private readonly IRestrictingDefinitionKindValidator _restrictingKinds;

    /// <summary>
    /// Construct over the resolver + registry. <paramref name="requiredClasses"/> are the
    /// tag codes treated as class-required (refuse-on-absence); defaults to the predefined
    /// <c>pii</c> / <c>identifier</c> / <c>phi</c> / <c>pci</c> / <c>cui</c>.
    /// </summary>
    public PolicyAdmissionValidator(
        IAspectResolver resolver,
        IPolicyRegistry registry,
        IReadOnlyCollection<string>? requiredClasses = null,
        IRestrictingDefinitionKindValidator? restrictingKinds = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _restrictingKinds = restrictingKinds ?? RestrictingDefinitionKindValidator.Shared;
        _requiredClasses = requiredClasses ?? new[]
        {
            PredefinedPolicyBindings.Pii.Code,
            // retro #1685 F1: identifier is the per-subject crypto-shred class (ADR 0135 GDPR
            // erasure) — the HIGHEST-stakes data-classification kind. It was absent from the
            // default class-required set, so an identifier tag against a custom registry that
            // stripped its binding would escape the refuse-on-absence (2b) guard and silently
            // carry cleartext. It is class-required for the same reason pii/phi/pci/cui are.
            PredefinedPolicyBindings.Identifier.Code,
            PredefinedPolicyBindings.Phi.Code,
            PredefinedPolicyBindings.Pci.Code,
            PredefinedPolicyBindings.Cui.Code,
        };
    }

    /// <summary>The recognised codes within the predefined <c>shipyard/data-classification</c>
    /// system (pii / identifier / phi / pci / cui). A tag in THAT system whose code is not one of
    /// these is a forged/typo'd predefined tag — rejected fail-closed regardless of registry state
    /// (a genuinely custom SYSTEM stays open-vocab). Independent of whether a binding is present,
    /// so a known kind against a stripped registry still falls through to refuse-on-absence.</summary>
    private static readonly HashSet<string> KnownDataClassificationCodes =
        PredefinedPolicyBindings.All
            .Where(b => PredefinedPolicyBindings.IsDataClassificationSystem(b.Tag.System))
            .Select(b => b.Tag.Code)
            .ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public void ValidateAtPublish(
        FormDefinition def,
        IReadOnlyList<FormDefinition>? ancestors = null,
        IReadOnlyList<string>? regimePrecedence = null)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));

        foreach (var field in def.Overlay.Fields.Keys)
        {
            // (1) Resolve — surfaces relax-attempt / unsatisfiable-residency / regime-conflict.
            var policy = _resolver.ResolvePolicy(def, field, ancestors, regimePrecedence);

            // (2) Refuse-on-absence + class-coverage for every class-required resolved tag.
            foreach (var tag in policy.Tags)
            {
                // Normalize obvious whitespace: leading/trailing whitespace in a coding-system id is a
                // non-semantic artifact, never a distinct custom vocabulary. Compare the TRIMMED system
                // so a pure trailing-space `shipyard/data-classification ` is treated AS the predefined
                // system below (subject to the unknown-kind check) instead of escaping to open-vocab or
                // reading as a near-miss. Binding resolution below still keys off the raw tag, so a
                // whitespace-dirty predefined tag whose stored form cannot resolve at runtime is rejected
                // fail-closed (2b), never admitted as protected on the strength of trimming alone.
                var trimmedSystem = tag.System.Trim();
                var isPredefinedSystem = PredefinedPolicyBindings.IsDataClassificationSystem(trimmedSystem);

                // (2a′) FAIL-CLOSED near-miss SYSTEM guard (retro #1685 F1). A system that is NOT the
                // predefined data-classification system but closely RESEMBLES one of its ACCEPTED spellings
                // (a case-fold match or a small edit distance — e.g. a `...classificaton` slip, or a case
                // variant of either accepted spelling) is almost certainly a typo of it. NOTE the threshold
                // is small on purpose: a spelling that differs in the whole PREFIX is many edits away and is
                // NOT caught here — which is exactly why the rename adds the new id to the accepted set
                // instead of swapping the literal (a bare swap would leave every stored tag in the retired
                // spelling as an unbound open-vocab system: fail-SOFT, no encryption/redaction/audit).
                // Left as open-vocab a near miss would
                // resolve to NO binding, is not class-required, and would silently carry the field with no
                // encryption/redaction/audit — the asymmetric fail-soft the retro flags (a typo'd CODE is
                // rejected by 2a, but a typo'd SYSTEM would fail-soft). Reject with a distinct, stable code
                // so the author disambiguates — SYMMETRIC with 2a. A genuinely-distinct custom system (edit
                // distance beyond the threshold, not a case variant) still admits as open-vocab.
                if (!isPredefinedSystem && IsSuspectDataClassificationSystem(trimmedSystem))
                {
                    throw new FormDefinitionValidationException(def.Id,
                        $"aspect.suspect_system: field '{field}' carries tag '{tag.Code}' in system " +
                        $"'{tag.System}', which closely resembles the predefined " +
                        $"'{PredefinedPolicyBindings.DataClassificationSystem}' system but is not exactly it " +
                        "(nor any other accepted spelling of it). " +
                        "Use the predefined system to classify this field, or rename to a clearly distinct " +
                        "custom system if it is intentionally not data-classification.");
                }

                // (2a) FAIL-CLOSED on an unknown KIND in the PREDEFINED data-classification system:
                // a tag claiming `shipyard/data-classification` but whose code is not a recognised
                // predefined kind (pii/identifier/phi/pci/cui) is a forged/typo'd predefined tag that
                // would silently carry cleartext (no policy binds it). Reject the whole publish — a
                // genuinely custom SYSTEM stays open-vocab. Keyed off the known-code SET (not the
                // registry), so a known kind against a stripped registry still falls through to (2b).
                if (isPredefinedSystem && !KnownDataClassificationCodes.Contains(tag.Code))
                {
                    throw new FormDefinitionValidationException(def.Id,
                        $"aspect.unknown_kind: field '{field}' carries tag '{tag.Code}' in the predefined " +
                        $"'{PredefinedPolicyBindings.DataClassificationSystem}' system but no such kind exists.",
                        FormDefinitionCodes.AspectUnknownKind,
                        field);
                }

                var isRequiredClass = _requiredClasses.Contains(tag.Code, StringComparer.Ordinal);
                var binding = _registry.Resolve(tag);

                if (binding is null)
                {
                    // (2b) A class-required tag that resolves to NO policy ⇒ reject (never default-allow).
                    if (isRequiredClass)
                    {
                        throw new FormDefinitionValidationException(def.Id,
                            $"aspect.unclassified_required: field '{field}' carries class-required tag " +
                            $"'{tag.Code}' but no policy binding exists for it.");
                    }
                    continue; // a custom, non-class tag with no binding is permitted (no effects).
                }

                foreach (var effect in binding.Effects)
                {
                    EnsureKnownPolicyKind(def, tag.Code, effect.Kind.ToString());
                }

                // A binding declaring a class must cover its required effects.
                if (binding.Class is { } cls)
                {
                    foreach (var req in cls.RequiredEffects)
                    {
                        EnsureKnownPolicyKind(def, tag.Code, req.Kind.ToString());
                        if (!Covers(binding, req))
                        {
                            throw new FormDefinitionValidationException(def.Id,
                                $"aspect.class_coverage: field '{field}' tag '{tag.Code}' (class '{cls.Name}') " +
                                $"binding is missing required effect '{req.Kind}'" +
                                (req.Trigger is { } t ? $"@{t}" : string.Empty) + ".");
                        }
                    }
                }
            }
        }

        // (3) PII-aware async-check gating (ADR 0140 D2 §6 — item 6). An async check hands the
        // target field + every input field's VALUE to a host-registered connector. A field whose
        // EFFECTIVE classification is a sensitive class (a binding carrying a TagClass) must NOT be
        // fed to a connector unless the author explicitly acknowledged it — else a tagged value
        // leaks off-node silently. Fail-closed: reject the publish unless AllowsSensitiveInputs.
        ValidateAsyncChecksNotFeedingUnacknowledgedSensitiveInputs(def, ancestors, regimePrecedence);
    }

    private void EnsureKnownPolicyKind(FormDefinition def, string bindingId, string kind)
    {
        var refusal = _restrictingKinds.Validate(
            RestrictingDefinitionKindFamily.Policy,
            def.Id.Value,
            kind,
            nestedDefinitionId: bindingId);
        if (refusal is not null)
        {
            throw new FormDefinitionValidationException(
                def.Id,
                refusal.Message,
                refusal.Code,
                bindingId);
        }
    }

    private void ValidateAsyncChecksNotFeedingUnacknowledgedSensitiveInputs(
        FormDefinition def, IReadOnlyList<FormDefinition>? ancestors, IReadOnlyList<string>? regimePrecedence)
    {
        var checks = def.Overlay.AsyncChecks;
        if (checks is not { Count: > 0 }) return;

        foreach (var check in checks)
        {
            if (check.AllowsSensitiveInputs) continue; // author acknowledged; permitted.

            // The connector receives the target field's value + every declared input's value.
            var fed = new List<string> { check.Field };
            if (check.Inputs is { Count: > 0 }) fed.AddRange(check.Inputs);

            foreach (var fedField in fed.Distinct(StringComparer.Ordinal))
            {
                if (!def.Overlay.Fields.ContainsKey(fedField)) continue; // structural check owns unknown-field.
                var policy = _resolver.ResolvePolicy(def, fedField, ancestors, regimePrecedence);
                var sensitive = policy.Tags.FirstOrDefault(t => _registry.Resolve(t)?.Class is not null);
                if (sensitive is not null)
                {
                    throw new FormDefinitionValidationException(def.Id,
                        $"aspect.sensitive_input_unacknowledged: async check '{check.Id}' feeds field " +
                        $"'{fedField}' (classified '{sensitive.Code}') to connector '{check.Connector}' " +
                        "without allowsSensitiveInputs. Acknowledge it or remove the field from the check.",
                        FormDefinitionCodes.AspectSensitiveInputUnacknowledged,
                        fedField);
                }
            }
        }
    }

    private static bool Covers(PolicyBinding binding, RequiredEffect req)
        => binding.Effects.Any(e =>
            e.Kind == req.Kind &&
            (req.Trigger is null || e.Triggers.Contains(req.Trigger.Value)));

    /// <summary>The maximum case-insensitive edit distance from an accepted predefined
    /// data-classification system id at which a NON-matching system id is treated as a
    /// suspect typo rather than a genuinely-distinct custom system. Small (2) so it catches one or two
    /// character slips / a transposition without ever flagging a clearly-distinct custom system (whose
    /// own prefix puts it many edits away — e.g. <c>acme/data-classification</c> is 8 edits distant).</summary>
    private const int SuspectSystemEditDistanceThreshold = 2;

    /// <summary>
    /// True when <paramref name="system"/> (already whitespace-trimmed and known NOT ordinal-equal to
    /// an accepted one) is a near-miss of ANY accepted spelling of the predefined
    /// data-classification system - a case-only variant, or within
    /// <see cref="SuspectSystemEditDistanceThreshold"/> case-insensitive edits of one of them.
    /// Checking EVERY accepted spelling is what keeps the validator closed on both sides of the
    /// ticket-260 rename window: a typo of the renamed id is rejected exactly as a typo of the
    /// retired id is, instead of falling through to open-vocab.
    /// </summary>
    private static bool IsSuspectDataClassificationSystem(string system)
    {
        foreach (var canonical in PredefinedPolicyBindings.AcceptedDataClassificationSystems)
        {
            // A pure case variant is the most common near-miss - always a suspect.
            if (string.Equals(system, canonical, StringComparison.OrdinalIgnoreCase))
                return true;

            // Otherwise a small case-insensitive edit distance is a typo of that predefined spelling.
            if (WithinEditDistance(system, canonical, SuspectSystemEditDistanceThreshold))
                return true;
        }

        return false;
    }

    /// <summary>Bounded, case-insensitive Levenshtein distance test: true iff editing
    /// <paramref name="a"/> into <paramref name="b"/> takes at most <paramref name="threshold"/> single
    /// -character insert/delete/substitute operations. Two-row DP with a length-delta fast-reject and a
    /// per-row early-out so it never does more work than the small threshold warrants.</summary>
    private static bool WithinEditDistance(string a, string b, int threshold)
    {
        if (Math.Abs(a.Length - b.Length) > threshold) return false; // length gap alone exceeds budget.

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > threshold) return false; // every cell in this row already over budget.
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length] <= threshold;
    }
}
