using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// Read-only persistence surface for Harborline asset-model entities.
/// </summary>
/// <remarks>
/// <para>Spec §3.1. Phase A plan notes:</para>
/// <list type="bullet">
///   <item><description>
///     <b>D-VERSION-STORE-SHAPE:</b> <see cref="IEntityMutationStore.UpdateAsync(EntityId, ValidatedRecordBody, UpdateOptions, CancellationToken)"/> accepts a full body, not a JSON Patch.
///     The patch-flavoured signature from spec §3.1 is deferred to Phase B.
///   </description></item>
///   <item><description>
///     <b>D-EXTENSIBILITY-SEAMS:</b> validator / observer / audit-context are injected as optional
///     services; Phase A ships null-object defaults.
///   </description></item>
/// </list>
/// </remarks>
public interface IEntityStore
{
    /// <summary>
    /// Reads an entity. Default <paramref name="version"/> returns the latest non-deleted version;
    /// see <see cref="VersionSelector"/> for explicit-sequence and as-of reads.
    /// </summary>
    Task<Entity?> GetAsync(EntityId id, VersionSelector version = default, CancellationToken ct = default);

    /// <summary>Streams entities matching the given filter.</summary>
    IAsyncEnumerable<Entity> QueryAsync(EntityQuery query, CancellationToken ct = default);
}

/// <summary>
/// Raw entity persistence used only beneath admitted write coordinators. This port is deliberately
/// not registered in DI; production consumers resolve <see cref="IEntityStore"/>, which is read-only.
/// </summary>
/// <remarks>
/// Two write seams (ticket 366 slice 1). The RECORD seam is public and takes a
/// <see cref="ValidatedRecordBody"/>, so persistence cannot be reached without the gate-then-validation
/// stages that mint one. The raw-body seam is <c>internal</c>, visible only to the assemblies that own a
/// NON-record write shape — a definition envelope or a form instance, each keeping its own admission
/// because a record schema cannot judge it. <c>Harborline.Api.LocalNodeHost</c> is deliberately not in
/// that friend set: a raw record write there does not compile.
/// </remarks>
public interface IEntityMutationStore : IEntityStore
{
    /// <summary>
    /// Mints a new record from a validated body. Idempotent on <c>(Scheme, Authority, Nonce, Issuer)</c>
    /// exactly as the raw seam is.
    /// </summary>
    // holds RW-9: the signature, not an allow-list, is what keeps an unvalidated record body out.
    Task<EntityId> CreateAsync(ValidatedRecordBody body, CreateOptions options, CancellationToken ct = default);

    /// <summary>
    /// Appends a new version from a validated body. The token's <see cref="ValidatedRecordBody.Schema"/>
    /// must equal the stored record's schema: an update validated against a different schema is refused
    /// rather than persisted.
    /// </summary>
    Task<VersionId> UpdateAsync(EntityId id, ValidatedRecordBody newBody, UpdateOptions options, CancellationToken ct = default);

    /// <summary>
    /// Mints a new entity. Idempotent on <c>(Scheme, Authority, Nonce, Issuer)</c> — repeating
    /// the call with the same tuple and same body returns the same <see cref="EntityId"/> rather
    /// than minting a duplicate. A matching tuple with a different body raises
    /// <see cref="IdempotencyConflictException"/>.
    /// </summary>
    internal Task<EntityId> CreateAsync(SchemaId schema, JsonDocument body, CreateOptions options, CancellationToken ct = default);

    /// <summary>
    /// Mints a batch of entities in a single call.
    /// </summary>
    /// <param name="drafts">The drafts to create, each specifying a schema, body, and options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The <see cref="EntityId"/> list corresponding 1-to-1 with <paramref name="drafts"/>,
    /// in the same order.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Atomicity is backend-dependent.</b> The default interface implementation (DIM) is a
    /// sequential loop over <see cref="CreateAsync(SchemaId, JsonDocument, CreateOptions, CancellationToken)"/>. It is <em>not atomic</em>: a failure on
    /// draft <c>N</c> leaves drafts <c>0..N-1</c> already committed. Backend implementations
    /// that can provide true all-or-nothing semantics (e.g. <c>InMemoryEntityStore</c>) override
    /// this method to do so.
    /// </para>
    /// <para>
    /// Postgres: the DIM fallback is in effect until a dedicated atomic override ships
    /// (<b>G21 follow-up</b>). Do not rely on rollback semantics when targeting the Postgres
    /// backend in the current release.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<EntityId>> CreateBatchAsync(
        IEnumerable<EntityDraft> drafts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var results = new List<EntityId>();
        foreach (var draft in drafts)
        {
            ct.ThrowIfCancellationRequested();
            var id = await CreateAsync(draft.Schema, draft.Body, draft.Options, ct).ConfigureAwait(false);
            results.Add(id);
        }
        return results;
    }

    /// <summary>
    /// Appends a new version with the given body and returns its <see cref="VersionId"/>.
    /// The entity's materialized current body is updated atomically.
    /// </summary>
    internal Task<VersionId> UpdateAsync(EntityId id, JsonDocument newBody, UpdateOptions options, CancellationToken ct = default);

    /// <summary>
    /// Inserts a tombstone version. Reads via the default selector return <c>null</c> afterwards,
    /// but as-of reads with an instant before the delete time still return the pre-delete entity.
    /// </summary>
    Task DeleteAsync(EntityId id, DeleteOptions options, CancellationToken ct = default);

}

/// <summary>Raised when the mutation port's create seam detects a conflicting duplicate mint.</summary>
public sealed class IdempotencyConflictException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public IdempotencyConflictException(string message) : base(message) { }
}

/// <summary>Raised when an optimistic-concurrency guard fails in the mutation port's update seam.</summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public ConcurrencyException(string message) : base(message) { }
}
