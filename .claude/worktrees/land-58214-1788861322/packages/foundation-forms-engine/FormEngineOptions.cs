namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// Tunable bounds and rendering preferences for a <see cref="FormEngine"/>
/// (ADR 0055 §3.1; INV-S2).
/// </summary>
/// <remarks>
/// <para>
/// <b>INV-S2 siting (per Admiral ruling 2026-05-31).</b> The two genuinely
/// engine-side resource bounds live here: a candidate-byte ceiling
/// (<see cref="MaxCandidateBytes"/>, a cheap pre-reject before the document
/// ever reaches the schema evaluator) and a validation wall-clock budget
/// (<see cref="ValidationBudget"/>). The wall-clock budget is
/// <i>defense-in-depth, not the ReDoS control</i>: a
/// <see cref="System.Threading.CancellationToken"/> / <c>Task.Delay</c> race
/// cannot abort a synchronous catastrophic <c>Regex.Match</c> already burning a
/// thread — it only bounds the caller's wait for a genuinely asynchronous
/// registry backend. The real ReDoS control is registry-sited (a process-level
/// regex match-timeout caught in <c>InMemorySchemaRegistry.ValidateAsync</c>),
/// as are the schema size / <c>$ref</c>-depth caps; the engine holds no schema
/// text, so those bounds have nowhere to live here.
/// </para>
/// </remarks>
public sealed class FormEngineOptions
{
    /// <summary>Default candidate-document byte ceiling: 1 MiB.</summary>
    public const int DefaultMaxCandidateBytes = 1 * 1024 * 1024;

    /// <summary>
    /// Maximum size, in UTF-8 bytes, of a candidate document accepted by
    /// <see cref="IFormEngine.ValidateAsync"/> / <see cref="IFormEngine.SaveAsync"/>.
    /// A candidate larger than this is rejected as an INV-S2
    /// <see cref="ValidationErrorKind.ResourceBound"/> failure before the
    /// schema evaluator runs.
    /// </summary>
    public int MaxCandidateBytes { get; init; } = DefaultMaxCandidateBytes;

    /// <summary>
    /// Wall-clock budget for a single schema-validation call. Defense-in-depth
    /// only (see the type remarks): bounds the caller's wait for an
    /// asynchronous registry backend; does not abort a synchronous catastrophic
    /// match. Defaults to two seconds.
    /// </summary>
    public TimeSpan ValidationBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ordered locale-preference chain used to resolve
    /// <see cref="Harborline.Api.Foundation.Forms.Models.InternationalizedText"/> when
    /// building a <see cref="FormView"/>. Defaults to <c>["en"]</c>.
    /// </summary>
    public IReadOnlyList<string> LocaleChain { get; init; } = new[] { "en" };

    /// <summary>
    /// When <see langword="true"/>, the <see cref="FormEngine"/> constructor fails
    /// closed (throws) unless the SPINE-2 governance seam (an
    /// <c>IFieldPolicyEnforcer</c> + an <c>IAspectResolver</c>) is wired. This is
    /// the composition-root gate (F-11 / ADR 0140 D2): a host that goes LIVE with
    /// classification-bearing forms sets this so the engine can never silently fall
    /// back to the legacy <c>PiiSensitivity</c>-only path — the exact
    /// "live service + silent-default" hole the gate exists to close. Defaults to
    /// <see langword="false"/> so the pre-governance composition (and every existing
    /// engine test) stays byte-identical.
    /// </summary>
    public bool RequireGovernanceEnforcement { get; init; }

    /// <summary>
    /// The residency jurisdiction the node persists field values in — the
    /// <c>TargetJurisdiction</c> the SPINE-2 Store PEP checks a classified field's
    /// residency eligibility against. Only consulted for fields whose resolved policy
    /// carries a <c>Reside</c> effect; ignored otherwise. Defaults to <c>"US"</c>;
    /// the host overrides it with its actual deployment region.
    /// </summary>
    public string HostJurisdiction { get; init; } = "US";
}
