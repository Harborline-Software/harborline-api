using System.Collections.Concurrent;

namespace Harborline.Api.Foundation.Documents.Issuance;

/// <summary>
/// Persistence for issued-document records (#111 §3.6). The store enforces the immutability invariant: a
/// second write for an existing <see cref="IssuedDocumentRecord.DocumentId"/> is rejected — an issued money
/// document is never overwritten (§0/§4.2).
/// </summary>
public interface IIssuedDocumentStore
{
    /// <summary>Persists a freshly-minted issued document. Throws if the id already exists (immutable).</summary>
    ValueTask AddAsync(IssuedDocumentRecord record, CancellationToken ct = default);

    /// <summary>Fetches an issued document by id, or null.</summary>
    ValueTask<IssuedDocumentRecord?> GetAsync(string documentId, CancellationToken ct = default);

    /// <summary>Lists the issued documents minted from a given source record (e.g. all documents for one invoice).</summary>
    ValueTask<IReadOnlyList<IssuedDocumentRecord>> ListForRecordAsync(string recordType, string recordId, CancellationToken ct = default);
}

/// <summary>
/// An in-memory <see cref="IIssuedDocumentStore"/> — the cluster default; the node overrides with a durable
/// EF-backed store. Enforces immutability by rejecting a duplicate id.
/// </summary>
public sealed class InMemoryIssuedDocumentStore : IIssuedDocumentStore
{
    private readonly ConcurrentDictionary<string, IssuedDocumentRecord> _byId = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask AddAsync(IssuedDocumentRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_byId.TryAdd(record.DocumentId, record))
        {
            throw new InvalidOperationException(
                $"An issued document '{record.DocumentId}' already exists — issued documents are immutable and "
                + "are never overwritten (a correction is a new document).");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IssuedDocumentRecord?> GetAsync(string documentId, CancellationToken ct = default)
        => ValueTask.FromResult(_byId.TryGetValue(documentId, out var record) ? record : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IssuedDocumentRecord>> ListForRecordAsync(string recordType, string recordId, CancellationToken ct = default)
    {
        IReadOnlyList<IssuedDocumentRecord> matches = _byId.Values
            .Where(r => string.Equals(r.RecordType, recordType, StringComparison.Ordinal)
                     && string.Equals(r.RecordId, recordId, StringComparison.Ordinal))
            .OrderBy(r => r.IssuedAtUtc)
            .ToList();
        return ValueTask.FromResult(matches);
    }
}
