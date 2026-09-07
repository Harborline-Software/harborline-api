namespace Harborline.Api.Foundation.ReportDefinitions;

/// <summary>Registers and reads immutable, descriptor-admitted report definition revisions.</summary>
public interface IReportDefinitionRegistry
{
    /// <summary>
    /// Canonicalizes and admits <paramref name="definition"/>, then stores it at its pinned
    /// <c>(tenant, key, version)</c> tuple. Identical registration is idempotent; divergent
    /// content raises <see cref="ReportDefinitionGovernanceException"/>.
    /// </summary>
    /// <param name="definition">The authored report definition.</param>
    /// <param name="cancellationToken">A token that cancels registration.</param>
    /// <returns>The stored canonical report definition.</returns>
    ValueTask<ReportDefinition> RegisterAsync(
        ReportDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the exact pinned <c>(tenant, key, version)</c> tuple, the reverse of
    /// <see cref="RegisterAsync"/>. Pack replacement retracts every definition of the replaced package
    /// before the replacement is admitted, so no partial override of a replaced package survives (L633).
    /// </summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="key">The stable definition key.</param>
    /// <param name="version">The immutable definition version.</param>
    /// <param name="cancellationToken">A token that cancels the removal.</param>
    /// <returns><see langword="true"/> when a definition was present and removed.</returns>
    ValueTask<bool> RemoveAsync(
        string tenant,
        string key,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an exact pinned tuple, or returns <see langword="null"/> when absent.</summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="key">The stable definition key.</param>
    /// <param name="version">The immutable definition version.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The stored definition, or <see langword="null"/>.</returns>
    ValueTask<ReportDefinition?> GetDefinitionAsync(
        string tenant,
        string key,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tenant's definitions as one head revision per key, keys ordered ordinally
    /// ascending. The head is the highest version under the same pinned ordering rule
    /// <see cref="ListVersionsAsync"/> documents. Returns an empty list for an unknown tenant.
    /// </summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The tenant's head revisions, one per key.</returns>
    ValueTask<IReadOnlyList<ReportDefinition>> ListDefinitionsAsync(
        string tenant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every admitted revision of one key, newest first, under the pinned ordering rule:
    /// when every version string for the key is a strict numeric MAJOR.MINOR.PATCH triple the
    /// order is semver-descending; any other shape switches the whole key to ordinal-string
    /// descending. The result names the rule applied, because the store records no admission
    /// order and the rule is the only thing making history deterministic across processes.
    /// Returns <see langword="null"/> when the tenant has no revision of the key.
    /// </summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="key">The stable definition key.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The ordered history and its ordering rule, or <see langword="null"/>.</returns>
    ValueTask<ReportDefinitionVersionList?> ListVersionsAsync(
        string tenant,
        string key,
        CancellationToken cancellationToken = default);
}

/// <summary>One key's admitted revisions, newest first, with the ordering rule that produced them.</summary>
/// <param name="Ordering">The rule applied: <c>semver</c> or <c>ordinal</c>.</param>
/// <param name="Versions">The revisions, newest first.</param>
public sealed record ReportDefinitionVersionList(
    string Ordering,
    IReadOnlyList<ReportDefinition> Versions);

/// <summary>Raised when report-definition content violates registry-owned governance.</summary>
public sealed class ReportDefinitionGovernanceException : InvalidOperationException
{
    /// <summary>Initializes an exception with a stable machine-readable error code.</summary>
    /// <param name="errorCode">The non-empty machine-readable error code.</param>
    public ReportDefinitionGovernanceException(string errorCode)
        : base($"Report definition governance refused: {errorCode}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable machine-readable error code.</summary>
    public string ErrorCode { get; }
}
