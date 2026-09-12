using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Admission;

/// <summary>
/// One composed content item presented to install-time admission — the FINAL post-install-cascade JSON
/// (seed ⊕ re-attached tenant overrides), NOT the pack item in isolation (S-9/F6: a workflow benign
/// alone can become effecting when composed with org config, so admission runs over the composed result).
/// </summary>
/// <param name="PackageKey">The package carrying the definition.</param>
/// <param name="Key">The content key.</param>
/// <param name="Kind">The declarative kind (routes to the right validator; only effecting kinds gate).</param>
/// <param name="Version">The pinned content version (identity stamping for the validator).</param>
/// <param name="CanonicalJson">The composed content's JSON — what would become live if installed.</param>
public sealed record PackComposedItem(
    string PackageKey,
    string Key,
    PackContentKind Kind,
    string Version,
    string CanonicalJson);

/// <summary>One admission refusal — the offending content key + a stable code + a locator/message.</summary>
/// <param name="ContentKey">The content key that failed admission.</param>
/// <param name="Code">The stable admission-violation code (e.g. an ADR 0143 <c>workflow.admission.*</c>).</param>
/// <param name="Message">A developer-facing message; a localizing client keys off <paramref name="Code"/>.</param>
public sealed record PackAdmissionRefusal(string ContentKey, string Code, string Message);

/// <summary>Published content-admission refusal codes.</summary>
public static class PackAdmissionCodes
{
    public const string NotWired = "pack.install.admission.not_wired";
}

/// <summary>The outcome of install-time admission — admissible, or the surviving refusals.</summary>
/// <param name="Refusals">Empty ⇒ every effecting item is admissible; non-empty ⇒ install is refused.</param>
public sealed record PackAdmissionResult(IReadOnlyList<PackAdmissionRefusal> Refusals)
{
    /// <summary>True iff no refusals survived.</summary>
    public bool IsAdmissible => Refusals.Count == 0;

    /// <summary>The admissible result (no refusals).</summary>
    public static PackAdmissionResult Admissible { get; } = new(Array.Empty<PackAdmissionRefusal>());
}

/// <summary>
/// Install-time admission for a pack's effecting content (S-9 / A7). Runs the FULL ADR 0143 workflow
/// admission validator — the SAME validator the authoring path uses, no pack-scoped subset — over the
/// COMPOSED post-install cascade, with classification RE-DERIVED from the INSTALLING instance's own
/// ADR 0128 capability→authority registry (the pack author's labels are non-authoritative; a mismatch is
/// refused, ADR 0143 F2).
/// </summary>
/// <remarks>
/// The concrete workflow validator lives in <c>blocks-workflow</c> (above the foundation tier), so this is
/// a PORT: the install engine calls it; the host binds the real adapter (<c>WorkflowDefinitionWireMapper</c>
/// + <c>WorkflowAdmissionValidator</c> over the node's registry). The foundation default is fail-closed —
/// see <see cref="WorkflowRefusingPackContentAdmission"/>.
/// </remarks>
public interface IPackContentAdmission
{
    /// <summary>Admits (or refuses) a pack's composed effecting content for a tenant. Never throws for an
    /// inadmissible pack — an inadmissible definition is a refusal in the result, not an exception.</summary>
    PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant);
}

/// <summary>
/// The FAIL-CLOSED foundation default <see cref="IPackContentAdmission"/>: it admits a pack that carries
/// NO effecting (workflow) content, and REFUSES any pack that carries a <see cref="PackContentKind.WorkflowDefinition"/>
/// — because a foundation-tier engine cannot run the ADR 0143 validator itself, and S-9 forbids installing
/// an effecting definition without install-time admission. A host that wants to install workflow-bearing
/// packs MUST bind the real adapter (the node host does). This makes "forgot to wire admission" a hard
/// refusal, never a silent skip.
/// </summary>
public sealed class WorkflowRefusingPackContentAdmission : IPackContentAdmission
{
    /// <summary>The refusal code emitted when an effecting item is present but no real admission is wired.</summary>
    public const string AdmissionNotWiredCode = PackAdmissionCodes.NotWired;

    private readonly PackRestrictingDefinitionAdmission _restricting;

    /// <summary>Constructs the fail-closed default over the shared restricting-kind validator.</summary>
    public WorkflowRefusingPackContentAdmission(IRestrictingDefinitionKindValidator? kinds = null)
        => _restricting = new PackRestrictingDefinitionAdmission(
            kinds ?? RestrictingDefinitionKindValidator.Shared);

    /// <inheritdoc />
    public PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var refusals = _restricting.Validate(composed).ToList();
        refusals.AddRange(PackNavigationContentAdmission.Validate(composed, tenant));
        refusals.AddRange(composed
            .Where(c => c.Kind == PackContentKind.WorkflowDefinition)
            .Select(c => new PackAdmissionRefusal(
                c.Key, PackAdmissionCodes.NotWired,
                "This pack carries an effecting workflow definition, but no ADR 0143 install-time admission "
                + "validator is wired. Fail-closed: refusing rather than installing an un-admitted effecting "
                + "definition (S-9).")));

        return refusals.Count == 0 ? PackAdmissionResult.Admissible : new PackAdmissionResult(refusals);
    }
}
