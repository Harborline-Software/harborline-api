namespace Harborline.Api.Foundation.ScheduleDefinitions;

/// <summary>Registers and reads immutable, descriptor-admitted schedule definition revisions.</summary>
public interface IScheduleDefinitionRegistry
{
    /// <summary>
    /// Canonicalizes and admits <paramref name="definition"/>, then stores it at its pinned
    /// <c>(tenant, key, version)</c> tuple. Identical registration is idempotent; divergent
    /// content raises <see cref="ScheduleDefinitionGovernanceException"/>.
    /// </summary>
    /// <param name="definition">The authored schedule definition.</param>
    /// <param name="cancellationToken">A token that cancels registration.</param>
    /// <returns>The stored canonical schedule definition.</returns>
    ValueTask<ScheduleDefinition> RegisterAsync(
        ScheduleDefinition definition,
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
    ValueTask<ScheduleDefinition?> GetDefinitionAsync(
        string tenant,
        string key,
        string version,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when schedule-definition content violates registry-owned governance.</summary>
public sealed class ScheduleDefinitionGovernanceException : InvalidOperationException
{
    /// <summary>Initializes an exception with a stable machine-readable error code.</summary>
    /// <param name="errorCode">The non-empty machine-readable error code.</param>
    public ScheduleDefinitionGovernanceException(string errorCode)
        : base($"Schedule definition governance refused: {errorCode}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable machine-readable error code.</summary>
    public string ErrorCode { get; }
}
