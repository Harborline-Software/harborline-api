using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// Persistence port for save-and-resume submission-drafts (ADR 0135 amendment
/// 2026-07-01 — D2). The in-memory reference implementation
/// (<see cref="InMemorySubmissionDraftStore"/>) ships here; a host swaps in a
/// durable, restart-surviving store (e.g. a node-EF SQLCipher-backed store) behind
/// this same interface for genuine cross-device resume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by the tuple.</b> Every operation addresses a draft by its full
/// <see cref="SubmissionDraftKey"/> — the store never mixes drafts across tenants or
/// parties. A durable implementation is responsible for encrypting the body at rest,
/// consulting the subject-erasure registry on read (an erased subject's draft is
/// treated as gone), and honouring legal-hold on any retention purge.
/// </para>
/// </remarks>
public interface ISubmissionDraftStore
{
    /// <summary>
    /// Inserts or replaces the draft at <paramref name="draft"/>.<see cref="SubmissionDraft.Key"/>
    /// (last-write-wins per key). Idempotent per key.
    /// </summary>
    Task UpsertAsync(SubmissionDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Returns the draft for <paramref name="key"/>, or null when none exists (or the
    /// draft's subject has been crypto-shredded — an erased subject reads as absent).
    /// </summary>
    Task<SubmissionDraft?> GetAsync(SubmissionDraftKey key, CancellationToken ct = default);

    /// <summary>Deletes the draft for <paramref name="key"/> (e.g. on abandon or after promotion). Returns true when a draft was removed.</summary>
    Task<bool> DeleteAsync(SubmissionDraftKey key, CancellationToken ct = default);

    /// <summary>
    /// Lists the resumable drafts a party owns in a tenant — the "my in-progress cases"
    /// resume surface, scoped by <em>party</em> (not merely tenant+role). Ordered
    /// most-recently-updated first.
    /// </summary>
    Task<IReadOnlyList<SubmissionDraft>> ListByPartyAsync(TenantId tenant, Guid partyId, CancellationToken ct = default);
}
