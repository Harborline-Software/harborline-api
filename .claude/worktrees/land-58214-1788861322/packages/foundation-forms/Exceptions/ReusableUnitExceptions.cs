using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Exceptions;

/// <summary>
/// Raised when <see cref="IReusableUnitStore"/> is asked for a unit that does not exist
/// (or not at the requested version / within the requested tenant boundary).
/// </summary>
public sealed class ReusableUnitNotFoundException : Exception
{
    /// <summary>The unit id that was looked up.</summary>
    public ReusableUnitId UnitId { get; }

    /// <summary>The version that was looked up, or <see langword="null"/> for a
    /// "current published" lookup.</summary>
    public SemanticVersion? Version { get; }

    /// <summary>The tenant boundary the lookup was scoped to.</summary>
    public TenantId Tenant { get; }

    /// <summary>Constructs the exception.</summary>
    public ReusableUnitNotFoundException(ReusableUnitId unitId, SemanticVersion? version, TenantId tenant)
        : base(version is null
            ? $"No published ReusableUnit with id '{unitId}' in tenant '{tenant}'."
            : $"No ReusableUnit with id '{unitId}' at version '{version}' in tenant '{tenant}'.")
    {
        UnitId = unitId;
        Version = version;
        Tenant = tenant;
    }
}

/// <summary>
/// Raised when <see cref="IReusableUnitStore.RegisterAsync"/> is called for a
/// (tenant, id, version) tuple that already exists. Unit revisions are immutable — to ship a
/// corrected revision, register a new version.
/// </summary>
public sealed class ReusableUnitConflictException : Exception
{
    /// <summary>The unit id that conflicted.</summary>
    public ReusableUnitId UnitId { get; }

    /// <summary>The version that conflicted.</summary>
    public SemanticVersion Version { get; }

    /// <summary>The tenant the conflict occurred within.</summary>
    public TenantId Tenant { get; }

    /// <summary>Constructs the exception.</summary>
    public ReusableUnitConflictException(ReusableUnitId unitId, SemanticVersion version, TenantId tenant)
        : base($"ReusableUnit '{unitId}' at version '{version}' is already registered in tenant '{tenant}'. Register a new version instead of overwriting.")
    {
        UnitId = unitId;
        Version = version;
        Tenant = tenant;
    }
}

/// <summary>
/// Raised when a reusable-unit registration violates a body invariant — a
/// form-component unit with no body / no items, a field item referencing an undeclared body
/// field, a nested reference inside a unit body, or an over-limit body tree. The
/// <see cref="Code"/> carries a stable localizable diagnostic.
/// </summary>
public sealed class ReusableUnitValidationException : Exception
{
    /// <summary>The unit id that failed validation.</summary>
    public ReusableUnitId UnitId { get; }

    /// <summary>Stable, locale-independent code (a <see cref="ReusableUnitCodes"/> or, for
    /// body-tree bounds, a <see cref="FormDefinitionCodes"/> <c>Tree*</c> constant).</summary>
    public string? Code { get; }

    /// <summary>Constructs the exception.</summary>
    public ReusableUnitValidationException(ReusableUnitId unitId, string message, string? code = null)
        : base($"ReusableUnit '{unitId}' failed validation: {message}")
    {
        UnitId = unitId;
        Code = code;
    }
}

/// <summary>
/// Raised when <see cref="IReuseResolver"/> cannot resolve a reference-cascade —
/// an unresolved reference, a workflow-subgraph reference from a form (Phase-1 fence), a
/// locked-field override, or a duplicate reused field. Fail-closed: a definition that cannot
/// be resolved cleanly is never partially expanded. The <see cref="Code"/> carries a stable
/// <see cref="ReusableUnitCodes"/> diagnostic.
/// </summary>
public sealed class ReuseResolutionException : Exception
{
    /// <summary>The consuming definition being resolved.</summary>
    public FormDefinitionId DefinitionId { get; }

    /// <summary>The referenced unit, when the failure concerns a specific reference.</summary>
    public ReusableUnitId? UnitId { get; }

    /// <summary>Stable, locale-independent <see cref="ReusableUnitCodes"/> code.</summary>
    public string Code { get; }

    /// <summary>Constructs the exception.</summary>
    public ReuseResolutionException(FormDefinitionId definitionId, ReusableUnitId? unitId, string code, string message)
        : base($"FormDefinition '{definitionId}' reuse resolution failed [{code}]: {message}")
    {
        DefinitionId = definitionId;
        UnitId = unitId;
        Code = code;
    }
}
