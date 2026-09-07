using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Admission;

/// <summary>
/// The fail-closed, publish-time admission validator (ADR 0140 D2 §3.4), modeled verbatim
/// on the ADR 0135 A1 validator: load-time + re-run-on-reload, refuse-on-absence
/// (unclassified-but-required ⇒ reject, never default-allow), and structural (it proves
/// the binding exists and covers the class's required effects; it does not judge whether a
/// value is "really" sensitive).
/// </summary>
public interface IPolicyAdmissionValidator
{
    /// <summary>
    /// Validate a definition at publish. Resolves every field (surfacing relax-attempts,
    /// unsatisfiable residency, and uncovered regime conflicts) and proves every
    /// class-required tag maps to a policy whose effects cover the class's required
    /// effects. Throws on the first violation; returns normally only when the whole
    /// definition admits.
    /// </summary>
    /// <exception cref="Harborline.Api.Foundation.Forms.Exceptions.FormDefinitionValidationException">
    /// Any resolution error, a class-required tag with no binding, or a binding missing a
    /// required effect.</exception>
    void ValidateAtPublish(
        FormDefinition def,
        IReadOnlyList<FormDefinition>? ancestors = null,
        IReadOnlyList<string>? regimePrecedence = null);
}
