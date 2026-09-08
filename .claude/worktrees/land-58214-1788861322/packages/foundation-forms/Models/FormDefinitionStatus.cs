namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Lifecycle status of a <see cref="FormDefinition"/> definition (ADR 0055
/// §"Schema Registry"). The CRDT-sync substrate (ADR 0028) respects this
/// field — only <see cref="Published"/> definitions affect production
/// rendering; <see cref="Draft"/> definitions sync but stay invisible to
/// the form engine; <see cref="Deprecated"/> definitions remain readable
/// but raise an authoring warning; <see cref="Withdrawn"/> definitions
/// reject new writes outright.
/// </summary>
public enum FormDefinitionStatus
{
    /// <summary>
    /// Unpublished and invisible to the form engine, but synchronized to peers. This is not an
    /// isolated staging or sandbox state; use pack preview and activation for promotion.
    /// </summary>
    Draft = 0,

    /// <summary>Live; the form engine renders against this version.</summary>
    Published = 1,

    /// <summary>Readable; new writes raise an authoring warning. Used when a
    /// successor version exists and consumers should migrate.</summary>
    Deprecated = 2,

    /// <summary>Read-only; new writes are rejected. Used when a definition
    /// is retired entirely (typically replaced or proven defective).</summary>
    Withdrawn = 3,
}
