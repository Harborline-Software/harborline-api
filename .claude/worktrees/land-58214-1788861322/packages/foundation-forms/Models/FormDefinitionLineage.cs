namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Optional extension lineage for a <see cref="FormDefinition"/> — declares
/// that this definition extends another definition by adding fields, sections,
/// or rules (ADR 0055 §"Type Customization Model", composing ADR 0005).
/// </summary>
/// <remarks>
/// <para>
/// Extension is purely declarative on the keystone substrate. The form
/// engine + entity store layered on top use the lineage to:
/// </para>
/// <list type="bullet">
/// <item><description>Render the parent's sections before the extension's
///   sections (familiar layout for users who know the parent type).</description></item>
/// <item><description>Allow a single entity instance to satisfy both the
///   parent definition and the extended definition's invariants — useful for the
///   property-equipment EXTEND pattern (an extended <c>Property</c> is
///   still a <c>Property</c> for system code that doesn't know about the
///   extension).</description></item>
/// <item><description>Surface the lineage in the audit trail when definition
///   evolution events fire.</description></item>
/// </list>
/// <para>
/// Extension is acyclic and shallow in v1 — a definition may extend at most
/// one parent, and chains of extension are limited to depth 3 (parent /
/// child / grandchild). The keystone store enforces the depth limit at
/// registration time.
/// </para>
/// </remarks>
/// <param name="ParentDefinitionId">The definition this definition extends. MUST refer
/// to a definition already registered in the same store.</param>
/// <param name="ParentVersion">Specific parent version this extension targets.
/// Pinned so that a parent's version bump does not silently shift the
/// extension's effective shape.</param>
public sealed record FormDefinitionLineage(
    FormDefinitionId ParentDefinitionId,
    SemanticVersion ParentVersion);
