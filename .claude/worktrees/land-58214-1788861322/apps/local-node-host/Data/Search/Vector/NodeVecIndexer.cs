using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The write side of the durable KG vector index (ADR 0135 KG-search F3-lift amendment, Slice 1b) — embeds a
/// record's text, then upserts the embedding into the index UNDER FIVE structural gates:
/// <list type="bullet">
///   <item><b>M1 (no-fake-as-real)</b> — the embedding artifact's <c>model</c> MUST be a registered REAL floor
///     id (<see cref="KgModelFloorGate.IsRegisteredFloor"/>); a stub sentinel or unknown id is REFUSED
///     (<see cref="KgFloorUnavailableException"/>). A fake embedding can never pin as genuine <c>bge-m3</c>.</item>
///   <item><b>G-5 (versioned projection)</b> — every row carries <c>model</c> + <c>modelVersion</c>; the vector
///     width MUST equal the floor's declared dimension or it is a hard fault.</item>
///   <item><b>G-3 (OnlineOnly ⇒ never-index)</b> — an <c>OnlineOnly</c> record is NEVER written (a no-op or a
///     delete of a pre-existing row), so its embedding never enters the local index.</item>
///   <item><b>G-6 (per-subject crypto-shred)</b> — the embedding is sealed under the record's per-subject
///     sub-key (<see cref="ISubjectFieldEncryptor"/>), so crypto-shredding the subject makes it undecryptable.
///     Encrypting for an already-erased subject fails closed (the artifact is dropped, not indexed).</item>
///   <item><b>Fresh/incremental</b> — an upsert replaces the prior row, and the GDPR / void / residency-change
///     delete paths remove the row + its derived vec0 entry, so the index never surfaces a stale embedding.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Binary quantization is the default encoding</b> (the spike scale unlock): the provider's float32 vector is
/// packed to a <c>bit</c> code (<see cref="BinaryQuantization.Pack"/>) BEFORE it is sealed — 24× smaller, and
/// the same packed bytes feed the real <c>vec0</c> bit column. The plaintext sealed under the subject key is the
/// packed code, not the float32 vector.
/// </para>
/// <para>
/// <b>The derived vec0 acceleration row is purged-on-shred and on delete.</b> When a real <c>vec0</c> table
/// exists (native present), this indexer also writes/deletes the cleartext bit code into it via the injected
/// <see cref="IVecAccelerationSink"/>. The durable per-subject-encrypted row is the boundary; the vec0 entry is
/// a rebuildable cache of it (deleted whenever the durable row is deleted, so a shredded subject leaves no
/// cleartext residue). A null sink (the brute-force-only host) means there is no cleartext acceleration table at
/// all, so there is nothing to purge.
/// </para>
/// </remarks>
public sealed class NodeVecIndexer
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;
    private readonly IKgEmbeddingProvider? _embedder;
    private readonly ISubjectFieldEncryptor _subjectEncryptor;
    private readonly IVecAccelerationSink? _accelerationSink;
    private readonly bool _allowStubModel;

    /// <summary>
    /// Construct the vector indexer.
    /// </summary>
    /// <param name="contextFactory">The node-local search EF context factory (the SQLCipher file).</param>
    /// <param name="embedder">
    /// The embedding provider (M1-fenced). NULL means no provider is registered — the ingest path then fails
    /// closed (<see cref="KgFloorUnavailableException"/> / <c>provider.kg_floor_unavailable</c>) rather than
    /// indexing anything, the production posture when no real floor is available.
    /// </param>
    /// <param name="subjectEncryptor">The per-subject field encryptor (G-6) — seals the packed embedding under the subject sub-key.</param>
    /// <param name="accelerationSink">The optional vec0 cleartext acceleration sink (null = brute-force-only host, no vec0 table).</param>
    /// <param name="allowStubModel">
    /// TEST-ONLY escape hatch — when true the M1 gate also admits the self-identifying stub model
    /// (<see cref="KgModelFloorGate.StubModelSentinel"/>) so CI can exercise the full index/clip/crypto path
    /// without the heavy model. A PRODUCTION composition leaves this false, so a stub artifact is REFUSED.
    /// </param>
    public NodeVecIndexer(
        IDbContextFactory<NodeLocalSearchDbContext> contextFactory,
        IKgEmbeddingProvider? embedder,
        ISubjectFieldEncryptor subjectEncryptor,
        IVecAccelerationSink? accelerationSink = null,
        bool allowStubModel = false)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _embedder = embedder;
        _subjectEncryptor = subjectEncryptor ?? throw new ArgumentNullException(nameof(subjectEncryptor));
        _accelerationSink = accelerationSink;
        _allowStubModel = allowStubModel;
    }

    /// <summary>
    /// Embeds + indexes a record's text. Returns true if the embedding was indexed; false if it was refused
    /// because the record is <c>OnlineOnly</c> (G-3 — never-index, and any pre-existing row is deleted).
    /// </summary>
    /// <exception cref="KgFloorUnavailableException">
    /// No embedding provider is registered (<c>provider.kg_floor_unavailable</c>), or the produced artifact's
    /// model is not a registered floor (M1), or the vector width does not match the floor dimension (G-5).
    /// </exception>
    /// <exception cref="FieldEncryptionDeniedAtIndexException">The subject is crypto-shredded — the embedding is not indexed (G-6).</exception>
    public async Task<bool> IndexRecordAsync(
        string recordId,
        string tenantId,
        string? subjectId,
        string text,
        SearchResidency residency,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(text);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(tenantId),
            "record",
            recordId);

        // G-3 structural half: an OnlineOnly record is NEVER embedded or indexed. Delete any pre-existing row.
        if (residency == SearchResidency.OnlineOnly)
        {
            await DeleteRecordAsync(tenantId, recordId, originatingDecision, ct).ConfigureAwait(false);
            return false;
        }

        // Fail-closed when no provider: never index an unverifiable artifact (provider.kg_floor_unavailable).
        if (_embedder is null)
        {
            throw new KgFloorUnavailableException(
                "(none)", "no embedding provider is registered — the host cannot verify embedding provenance");
        }

        var artifact = await _embedder.EmbedAsync(recordId, tenantId, subjectId, text, ct).ConfigureAwait(false);
        await IndexArtifactAsync(artifact, residency, originatingDecision, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Indexes a pre-produced embedding artifact directly (the seam Slice 1d's capability wire feeds). Runs the M1 +
    /// G-5 + G-6 gates. Public so a test / a future caller can index an artifact it already holds.
    /// </summary>
    public async Task IndexArtifactAsync(
        KgEmbeddingArtifact artifact,
        SearchResidency residency,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(artifact.TenantId),
            "record",
            artifact.RecordId);

        // G-3: an OnlineOnly artifact is never indexed.
        if (residency == SearchResidency.OnlineOnly)
        {
            await DeleteRecordAsync(artifact.TenantId, artifact.RecordId, originatingDecision, ct).ConfigureAwait(false);
            return;
        }

        // ── M1 (no-fake-as-real) — the model id MUST be a registered REAL floor (or the test stub escape). ──
        var floor = KgModelFloorGate.ResolveFloor(artifact.Model);
        var isStub = string.Equals(artifact.Model, KgModelFloorGate.StubModelSentinel, StringComparison.Ordinal);
        if (floor is null && !(isStub && _allowStubModel))
        {
            // A stub sentinel, an unknown id, or a fabricated label — REFUSED. A fake can never pin as bge-m3.
            throw new KgFloorUnavailableException(
                artifact.Model,
                isStub
                    ? "the deterministic stub model is not a registered floor — refuse to index it as real"
                    : "the artifact's model is not a registered embedding floor (M1 no-fake-as-real)");
        }

        // ── G-5 (versioned projection) — the vector width MUST match the floor's declared dimension. ──
        var declaredDimension = floor?.Dimension ?? (_embedder?.Dimension ?? artifact.Vector.Count);
        if (artifact.Vector.Count != declaredDimension)
        {
            throw new KgFloorUnavailableException(
                artifact.Model,
                $"vector width {artifact.Vector.Count} does not match model dimension {declaredDimension} (G-5)");
        }
        if (artifact.Dimension != artifact.Vector.Count)
        {
            throw new KgFloorUnavailableException(
                artifact.Model,
                $"artifact dimension {artifact.Dimension} disagrees with its vector width {artifact.Vector.Count} (G-5)");
        }

        // Binary-quantize (the default encoding) BEFORE sealing — the plaintext is the packed bit code.
        var packed = BinaryQuantization.Pack(artifact.Vector);

        // ── G-6 (per-subject crypto-shred) — seal the packed code under the record's per-subject sub-key. ──
        var subjectLabel = string.IsNullOrEmpty(artifact.SubjectId)
            ? VecIndexConstants.TenantWideSubject
            : artifact.SubjectId;
        var tenant = TenantId.FromString(artifact.TenantId);
        EncryptedField envelope;
        try
        {
            envelope = await _subjectEncryptor
                .EncryptForSubjectAsync(packed, tenant, new SubjectId(subjectLabel), ct)
                .ConfigureAwait(false);
        }
        catch (SubjectErasedException)
        {
            // The subject is already crypto-shredded — do not index (and remove any stale row). Fail closed.
            await DeleteRecordAsync(artifact.TenantId, artifact.RecordId, originatingDecision, ct).ConfigureAwait(false);
            throw new FieldEncryptionDeniedAtIndexException(artifact.RecordId, subjectLabel);
        }

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await dbCtx.VecRows
            .FirstOrDefaultAsync(v => v.TenantId == artifact.TenantId && v.RecordId == artifact.RecordId, ct)
            .ConfigureAwait(false);

        var nonce = envelope.Nonce.ToArray();
        var ciphertext = envelope.Ciphertext.ToArray();

        if (existing is null)
        {
            dbCtx.VecRows.Add(new VecRow
            {
                RecordId = artifact.RecordId,
                TenantId = artifact.TenantId,
                SubjectId = subjectLabel,
                Model = artifact.Model,
                ModelVersion = artifact.ModelVersion,
                Dimension = artifact.Dimension,
                EncryptedEmbedding = ciphertext,
                EmbeddingNonce = nonce,
                KeyVersion = envelope.KeyVersion,
                Residency = SearchResidency.Cache,
            });
        }
        else
        {
            existing.SubjectId = subjectLabel;
            existing.Model = artifact.Model;
            existing.ModelVersion = artifact.ModelVersion;
            existing.Dimension = artifact.Dimension;
            existing.EncryptedEmbedding = ciphertext;
            existing.EmbeddingNonce = nonce;
            existing.KeyVersion = envelope.KeyVersion;
            existing.Residency = SearchResidency.Cache;
        }

        await dbCtx.SaveChangesAsync(ct).ConfigureAwait(false);

        // Derived vec0 acceleration row (cleartext bit code) — only when a native acceleration sink exists.
        if (_accelerationSink is not null)
        {
            await _accelerationSink
                .UpsertAsync(artifact.RecordId, artifact.TenantId, packed, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a record's embedding from the index entirely — the durable per-subject-encrypted row AND (when a
    /// native acceleration table exists) its derived cleartext vec0 entry. The structural delete path FOR the
    /// GDPR-shred / void / archive / OnlineOnly-transition flows so a removed record leaves no embedding residue.
    /// </summary>
    public async Task DeleteRecordAsync(
        string tenantId,
        string recordId,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(tenantId),
            "record",
            recordId);

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await dbCtx.VecRows
            .FirstOrDefaultAsync(v => v.TenantId == tenantId && v.RecordId == recordId, ct)
            .ConfigureAwait(false);
        if (row is not null)
        {
            dbCtx.VecRows.Remove(row);
            await dbCtx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (row is not null && _accelerationSink is not null)
        {
            await _accelerationSink.DeleteAsync(recordId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Purges every indexed embedding for a crypto-shredded subject (G-6 — the index is not a shred bypass).
    /// Deletes the durable per-subject-encrypted rows AND (when present) their derived cleartext vec0 entries.
    /// Called from the subject-erasure flow alongside the registry tombstone, so even the partially-invertible
    /// cleartext acceleration cache leaves no residue. The remaining encrypted rows of OTHER subjects are
    /// untouched and stay decryptable.
    /// </summary>
    /// <returns>The number of records purged.</returns>
    public async Task<int> PurgeSubjectAsync(string tenantId, string subjectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await dbCtx.VecRows
            .Where(v => v.TenantId == tenantId && v.SubjectId == subjectId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return 0;
        }

        var recordIds = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            recordIds.Add(row.RecordId);
        }

        dbCtx.VecRows.RemoveRange(rows);
        await dbCtx.SaveChangesAsync(ct).ConfigureAwait(false);

        if (_accelerationSink is not null)
        {
            foreach (var id in recordIds)
            {
                await _accelerationSink.DeleteAsync(id, ct).ConfigureAwait(false);
            }
        }

        return rows.Count;
    }
}

/// <summary>
/// Thrown when the indexer refuses to index a record because its data subject has been crypto-shredded (G-6).
/// The embedding is not written; any stale row is removed. Distinct from <see cref="KgFloorUnavailableException"/>
/// (an M1 / dimension refusal) — this is the GDPR-erasure fail-closed path.
/// </summary>
public sealed class FieldEncryptionDeniedAtIndexException : Exception
{
    /// <summary>The record whose embedding was refused.</summary>
    public string RecordId { get; }

    /// <summary>The erased subject the record is keyed to.</summary>
    public string SubjectId { get; }

    /// <summary>Construct for a record + its erased subject.</summary>
    public FieldEncryptionDeniedAtIndexException(string recordId, string subjectId)
        : base($"record '{recordId}' not indexed — subject '{subjectId}' is crypto-shredded (G-6)")
    {
        RecordId = recordId;
        SubjectId = subjectId;
    }
}
