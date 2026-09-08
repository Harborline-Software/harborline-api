namespace Harborline.Api.Blocks.Reports.Exceptions;

/// <summary>
/// Thrown by <see cref="IReportCartridge{TParams,TResult}.ExecuteAsync"/>
/// when cartridge parameters fail validation (cross-tenant entity
/// ID, malformed period, missing required field, etc.). The runner
/// passes this exception through unwrapped — callers see the
/// original type so they can render parameter-validation errors with
/// per-field detail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant validation anchor (per council SE-A1, W#72 PR 1).</b>
/// Every cartridge that accepts entity-ID parameters (property ID,
/// customer ID, vendor ID, account ID, etc.) MUST validate that
/// those IDs belong to the same tenant as
/// <see cref="ReportExecutionContext.TenantId"/>. Use
/// <see cref="CrossTenantEntityId"/> to produce the canonical error
/// for cross-tenant entity references. Code review on PRs 2–6 MUST
/// reject any cartridge that performs a cross-tenant entity-ID check
/// without calling this factory.
/// </para>
/// </remarks>
public sealed class ReportParameterValidationException : System.Exception
{
    /// <summary>The parameter name (or field path) that failed validation.</summary>
    public string ParameterName { get; }

    /// <summary>Construct with the parameter name + human-readable reason.</summary>
    public ReportParameterValidationException(string parameterName, string reason)
        : base($"Report parameter '{parameterName}' failed validation: {reason}")
    {
        ParameterName = parameterName;
    }

    /// <summary>
    /// Canonical factory for cross-tenant entity-ID validation failures.
    /// </summary>
    /// <param name="paramName">The parameter field name that contained the foreign entity ID (e.g., <c>"ChartId"</c>).</param>
    /// <param name="entityType">The entity kind (e.g., <c>"ChartOfAccounts"</c>, <c>"Property"</c>, <c>"Customer"</c>).</param>
    /// <returns>A <see cref="ReportParameterValidationException"/> with a consistent cross-tenant message.</returns>
    public static ReportParameterValidationException CrossTenantEntityId(
        string paramName,
        string entityType)
        => new(paramName, $"{entityType} does not belong to the executing tenant.");
}
