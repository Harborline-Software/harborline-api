using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Exceptions;

/// <summary>
/// Raised when <see cref="IFormDefinitionStore"/> is asked for a schema
/// that does not exist (or does not exist at the requested version /
/// within the requested tenant boundary).
/// </summary>
public sealed class FormDefinitionNotFoundException : Exception
{
    /// <summary>The id that was looked up.</summary>
    public FormDefinitionId SchemaId { get; }

    /// <summary>The version that was looked up, or <see langword="null"/>
    /// when the caller asked for the "current published" version.</summary>
    public SemanticVersion? Version { get; }

    /// <summary>The tenant boundary the lookup was scoped to.</summary>
    public TenantId Tenant { get; }

    /// <summary>Constructs the exception.</summary>
    public FormDefinitionNotFoundException(FormDefinitionId schemaId, SemanticVersion? version, TenantId tenant)
        : base(version is null
            ? $"No published FormDefinition with id '{schemaId}' in tenant '{tenant}'."
            : $"No FormDefinition with id '{schemaId}' at version '{version}' in tenant '{tenant}'.")
    {
        SchemaId = schemaId;
        Version = version;
        Tenant = tenant;
    }
}

/// <summary>
/// Raised when an admitted form-definition registration is attempted
/// for a (tenant, id, version) tuple that already exists. Schema revisions
/// are immutable — overwriting an existing version is a programming error;
/// to ship a corrected revision, register a new version.
/// </summary>
public sealed class FormDefinitionConflictException : Exception
{
    /// <summary>The id that conflicted.</summary>
    public FormDefinitionId SchemaId { get; }

    /// <summary>The version that conflicted.</summary>
    public SemanticVersion Version { get; }

    /// <summary>The tenant the conflict occurred within.</summary>
    public TenantId Tenant { get; }

    /// <summary>Constructs the exception.</summary>
    public FormDefinitionConflictException(FormDefinitionId schemaId, SemanticVersion version, TenantId tenant)
        : base($"FormDefinition '{schemaId}' at version '{version}' is already registered in tenant '{tenant}'. Register a new version instead of overwriting.")
    {
        SchemaId = schemaId;
        Version = version;
        Tenant = tenant;
    }
}

/// <summary>
/// Raised when a registration attempt violates an overlay invariant — a
/// section references a field that does not appear in the overlay, two
/// sections share an id, a rule scope references a missing field, etc.
/// The exception message names the specific invariant violated.
/// </summary>
public sealed class FormDefinitionValidationException : Exception
{
    /// <summary>The id that failed validation.</summary>
    public FormDefinitionId SchemaId { get; }

    /// <summary>
    /// Optional stable, locale-independent code for this violation (a
    /// <see cref="FormDefinitionCodes"/> constant), so a client localizes off the
    /// code rather than the English <see cref="Exception.Message"/>. Populated for
    /// the ADR-0055 Rev-7 item-tree bounds (INV-S2 extension); <see langword="null"/>
    /// for the pre-Rev-7 message-only invariants.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Optional id of the offending node (field / section / container / page) this
    /// violation concerns, so the authoring client anchors the inline error ON that
    /// node structurally rather than regex-scraping it out of the English
    /// <see cref="Exception.Message"/> (#1686 deep review, Finding 4). Populated where
    /// the offender is known at the throw site (the builder's constraint synthesizer);
    /// <see langword="null"/> otherwise — the client keeps its message-scrape fallback.
    /// </summary>
    public string? Target { get; }

    /// <summary>Constructs the exception (message-only, pre-Rev-7 invariants).</summary>
    public FormDefinitionValidationException(FormDefinitionId schemaId, string message)
        : this(schemaId, message, null, null)
    {
    }

    /// <summary>Constructs the exception carrying a stable localizable
    /// <paramref name="code"/> (ADR 0055 Rev 7 item-tree bounds).</summary>
    public FormDefinitionValidationException(FormDefinitionId schemaId, string message, string? code)
        : this(schemaId, message, code, null)
    {
    }

    /// <summary>Constructs the exception carrying a stable localizable
    /// <paramref name="code"/> and the offending node <paramref name="target"/>
    /// (#1686 deep review, Finding 4).</summary>
    public FormDefinitionValidationException(FormDefinitionId schemaId, string message, string? code, string? target)
        : base($"FormDefinition '{schemaId}' failed overlay validation: {message}")
    {
        SchemaId = schemaId;
        Code = code;
        Target = target;
    }
}
