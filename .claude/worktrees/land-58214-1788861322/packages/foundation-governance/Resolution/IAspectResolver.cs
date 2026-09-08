using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>
/// Computes the effective aspect (and composed policy) for a field by walking the
/// form→section→field grains with the SPINE-2 per-class resolution semantics, rejecting
/// relax-attempts and unsatisfiable residency at publish (ADR 0140 D2 §2).
/// </summary>
/// <remarks>
/// Pure function of its inputs — no I/O. Cross-definition lineage (EXTENDS, depth ≤ 3) is
/// supplied as the <c>ancestors</c> chain (coarsest-first); the caller that owns the
/// definition store materialises it.
/// </remarks>
public interface IAspectResolver
{
    /// <summary>
    /// Resolve the effective aspect for <paramref name="field"/> in <paramref name="def"/>.
    /// </summary>
    /// <exception cref="Harborline.Api.Foundation.Forms.Exceptions.FormDefinitionValidationException">
    /// A relax-attempt (un-tag / widen-roles / shorten-retention / lower-immutability), an
    /// empty residency intersection, or a lineage depth &gt; 3.</exception>
    ResolvedAspect Resolve(FormDefinition def, string field, IReadOnlyList<FormDefinition>? ancestors = null);

    /// <summary>
    /// Resolve the effective aspect AND compose its tag policies into the trigger-indexed
    /// effect map, resolving residency / regime-precedence conflicts.
    /// </summary>
    /// <exception cref="Harborline.Api.Foundation.Forms.Exceptions.FormDefinitionValidationException">
    /// As <see cref="Resolve"/>, plus an uncovered regime conflict.</exception>
    ResolvedFieldPolicy ResolvePolicy(
        FormDefinition def,
        string field,
        IReadOnlyList<FormDefinition>? ancestors = null,
        IReadOnlyList<string>? regimePrecedence = null);
}
