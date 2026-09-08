using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.BruteForce;

/// <summary>
/// The deterministic, native-free KNN engine (ADR 0135 KG-search F3-lift amendment, Slice 1b) — a pure-C#
/// brute-force Hamming scan over the durable, per-subject-ENCRYPTED <c>search_vec_rows</c>, with the EXACT
/// <c>record_id</c> clip applied as a <b>pre-filter BEFORE distance</b> (G-1), decrypting each candidate's
/// binary code under its per-subject sub-key (G-6) only after it has passed the clip.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is NOT a stub that bypasses security.</b> It computes REAL nearest neighbours over the REAL stored
/// (encrypted) vectors with the SAME clip semantics as the native <c>vec0</c> path: it loads ONLY the rows the
/// scope authorizes (the clip's <c>record_id IN (…)</c> is the SQL pre-filter on the durable table), so a
/// forbidden record's vector never participates and the returned k is drawn from the allowed set even when the
/// global-nearest vectors are all forbidden (the spike R-1 no-neighbour-leak property). Because it operates on
/// the encrypted store and decrypts per-subject, it ALSO exercises G-6 end to end: a crypto-shredded subject's
/// row decrypts to nothing (the sub-key is gone), so it silently drops out of the candidate set — proving the
/// index is not a shred bypass without needing the native.
/// </para>
/// <para>
/// It is the engine the Intel CI host (no <c>vec0</c> native) runs, so the clip-exactness / G-6 / RRF security
/// tests are genuine. The native <c>vec0</c> path (<see cref="Sqlite.Vec0KnnEngine"/>) is the production
/// accelerator; an arch-fence asserts BOTH engines carry the clip's <c>record_id</c> pre-filter so neither can
/// become a side door.
/// </para>
/// </remarks>
public sealed class BruteForceKnnEngine : IVecKnnEngine
{
    private readonly ISubjectFieldDecryptor _subjectDecryptor;
    private readonly Func<TenantId, IDecryptCapability> _capabilityFactory;

    /// <summary>
    /// Construct bound to the per-subject decryptor (G-6) + a per-tenant decrypt-capability factory. The node is
    /// the authorized local reader (it holds the file DEK); the factory mints the tenant-scoped capability the
    /// decryptor validates against (a <c>FixedDecryptCapability</c> for the query tenant in production).
    /// </summary>
    public BruteForceKnnEngine(
        ISubjectFieldDecryptor subjectDecryptor, Func<TenantId, IDecryptCapability> capabilityFactory)
    {
        _subjectDecryptor = subjectDecryptor ?? throw new ArgumentNullException(nameof(subjectDecryptor));
        _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VecKnnHit>> KnnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        AuthorizedRecordScope scope,
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(queryVector);
        if (scope.IsEmpty)
        {
            return Array.Empty<VecKnnHit>();
        }

        var queryCode = BinaryQuantization.Pack(queryVector);
        var tenant = TenantId.FromString(tenantId);
        var decryptCapability = _capabilityFactory(tenant);

        // ── G-1 PRE-FILTER — load ONLY the rows the scope authorizes (record_id IN (…) on the durable table). ──
        // The clip's WHERE is the SOLE record-narrowing predicate; tenant_id is the in-file isolation check.
        var (clipSql, clipBinder) = VecRecordClip.Build(scope);

        var candidates = new List<(string RecordId, string SubjectId, byte[] Code, byte[] Nonce, int KeyVersion)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                SELECT record_id, subject_id, encrypted_embedding, embedding_nonce, key_version
                FROM {VecIndexConstants.VecRowsTableName}
                WHERE tenant_id = $tenant
                  AND {clipSql};
                """;
            cmd.Parameters.AddWithValue("$tenant", tenantId);
            clipBinder(cmd);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    (byte[])reader[2],
                    (byte[])reader[3],
                    reader.GetInt32(4)));
            }
        }

        // ── G-6 — decrypt each candidate under its per-subject sub-key AFTER the clip; a shredded subject's row
        //     decrypts to nothing and drops out. Distance is over the DECRYPTED cleartext bit code (Hamming). ──
        var hits = new List<VecKnnHit>(candidates.Count);
        foreach (var c in candidates)
        {
            ReadOnlyMemory<byte> plaintext;
            try
            {
                var field = new EncryptedField(c.Code, c.Nonce, c.KeyVersion);
                plaintext = await _subjectDecryptor
                    .DecryptForSubjectAsync(field, decryptCapability, tenant, new SubjectId(c.SubjectId), ct)
                    .ConfigureAwait(false);
            }
            catch (FieldDecryptionDeniedException)
            {
                // Subject crypto-shredded (or capability denied) — the embedding is unreadable; drop it. This is
                // the index-is-not-a-shred-bypass property exercised end to end.
                continue;
            }

            var distance = BinaryQuantization.Hamming(plaintext.Span, queryCode);
            hits.Add(new VecKnnHit(c.RecordId, distance));
        }

        // Nearest first; stable tie-break by record_id for deterministic ordering.
        return hits
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.RecordId, StringComparer.Ordinal)
            .Take(Math.Max(1, k))
            .ToArray();
    }
}
