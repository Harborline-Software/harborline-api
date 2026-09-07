namespace Harborline.Api.Kernel.Sync.Application;

/// <summary>
/// Supplies the current opaque CRDT state vector for a logical document so a
/// peer can encode only operations beyond that frontier.
/// </summary>
public interface IDeltaStateVectorProvider
{
    /// <summary>Return the current state vector for <paramref name="documentId"/>.</summary>
    /// <param name="documentId">Logical document identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The opaque CRDT state-vector bytes.</returns>
    ValueTask<ReadOnlyMemory<byte>> GetCurrentStateVectorAsync(
        string documentId,
        CancellationToken ct);
}
