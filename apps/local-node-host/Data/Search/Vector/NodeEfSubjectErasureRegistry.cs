using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The durable EF/SQLite <see cref="ISubjectErasureRegistry"/> (ADR 0135 GDPR direction; the #1378 M-1
/// durable-erasure-store fix) — replaces the restart-volatile <see cref="InMemorySubjectErasureRegistry"/> on a
/// production node. Persists each crypto-shred as a write-once <see cref="SubjectErasureRow"/> in the SAME
/// SQLCipher <c>local-node.db</c> file as the per-subject-encrypted KG index it gates (via
/// <see cref="NodeLocalSearchDbContext"/>), so a recorded shred SURVIVES a process restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is load-bearing.</b> The stored provider deletes the subject key; this registry prevents a
/// replacement key from being generated for that erased identity. The in-memory default forgets erasures on
/// restart and permits new writes for the subject. This durable store closes that hole; the
/// <c>RequireDurableErasureStores()</c> composition-root gate ensures the
/// node never boots on the volatile default.
/// </para>
/// <para>
/// <b>Append-only.</b> Grow-only — no removal API, mirroring the one-directional crypto-shred semantics. A
/// concurrent / redelivered <see cref="MarkErasedAsync"/> for the same subject is resolved by the unique
/// composite PK: exactly one insert wins (returns <c>true</c>); the loser is caught as a duplicate-key and
/// reported as already-erased (<c>false</c>) — the same idempotency contract as the in-memory default.
/// </para>
/// <para>
/// <b>Recovery evidence (T-1048, DES-0029 ck-6).</b> The mark also records the approval evidence, so the host's
/// recovery pass finishes an interrupted erasure without its request. <see cref="CompleteAsync"/> stages the
/// <c>SubjectErased</c> audit in the outbox and clears the evidence in one commit, so the evidence is never
/// cleared before the audit is durable, and a crash between them cannot happen. The evidence holds personal data
/// (the approver ids); only this class reads it.
/// </para>
/// </remarks>
public sealed class NodeEfSubjectErasureRegistry : ISubjectErasureRecoveryRegistry
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Construct bound to the search context factory (the SQLCipher file the gated index also lives in).</summary>
    public NodeEfSubjectErasureRegistry(
        IDbContextFactory<NodeLocalSearchDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.SubjectErasures
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenant.Value && r.SubjectId == subject.Value, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default) =>
        MarkAsync(tenant, subject, evidence: null, ct);

    /// <inheritdoc />
    public ValueTask<bool> MarkErasedAsync(
        TenantId tenant, SubjectId subject, SubjectErasureEvidence evidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return MarkAsync(tenant, subject, evidence, ct);
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsCompletedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.SubjectErasures
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenant.Value && r.SubjectId == subject.Value && r.CompletedAtUnixMs != null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SubjectErasureEvidence?> FindEvidenceAsync(
        TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.SubjectErasures
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenant.Value && r.SubjectId == subject.Value, ct)
            .ConfigureAwait(false);
        return row?.ApprovedAtUnixMs is null ? null : Evidence(row);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(SubjectId subject, AuditRecord erasedAudit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(erasedAudit);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tenant = erasedAudit.TenantId.Value;
        var row = await ctx.SubjectErasures
            .SingleOrDefaultAsync(r => r.TenantId == tenant && r.SubjectId == subject.Value, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("A subject erasure cannot complete before it is marked.");

        // The audit is secured and the evidence cleared by ONE commit: either both or neither.
        await NodeAuditOutbox.StageSignedRecordAsync(ctx, erasedAudit, ct).ConfigureAwait(false);
        row.ApprovingActorsJson = null;
        row.LegalBasis = null;
        row.ApprovedAtUnixMs = null;
        row.NextRecoveryAtUnixMs = null;
        row.CompletedAtUnixMs ??= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InterruptedSubjectErasure>> ListDueAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.SubjectErasures
            .AsNoTracking()
            .Where(r => r.CompletedAtUnixMs == null && r.ApprovedAtUnixMs != null)
            // Skip a row whose backoff has not elapsed, so rows that keep failing cannot fill every pass.
            .Where(r => r.NextRecoveryAtUnixMs == null || r.NextRecoveryAtUnixMs <= nowMs)
            .OrderBy(r => r.ApprovedAtUnixMs).ThenBy(r => r.TenantId).ThenBy(r => r.SubjectId)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => new InterruptedSubjectErasure(
            new TenantId(r.TenantId),
            new SubjectId(r.SubjectId),
            Evidence(r))).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DeferAsync(TenantId tenant, SubjectId subject, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.SubjectErasures
            .SingleOrDefaultAsync(r => r.TenantId == tenant.Value && r.SubjectId == subject.Value, ct)
            .ConfigureAwait(false);
        if (row is null || row.CompletedAtUnixMs is not null) return;
        row.RecoveryAttempts++;
        row.NextRecoveryAtUnixMs = (now + RecoveryBackoff(row.RecoveryAttempts)).ToUnixTimeMilliseconds();
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static SubjectErasureEvidence Evidence(SubjectErasureRow r) => new(
        (JsonSerializer.Deserialize<string[]>(r.ApprovingActorsJson ?? "[]") ?? []).Select(a => new ActorId(a)).ToArray(),
        r.LegalBasis ?? string.Empty,
        DateTimeOffset.FromUnixTimeMilliseconds(r.ApprovedAtUnixMs!.Value));

    /// <summary>One minute after the first failure, doubling per attempt, at most an hour. Never a give-up.</summary>
    internal static TimeSpan RecoveryBackoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));

    private async ValueTask<bool> MarkAsync(
        TenantId tenant, SubjectId subject, SubjectErasureEvidence? evidence, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Idempotent: a second mark for the same (tenant, subject) is a no-op that reports already-erased.
        var existing = await ctx.SubjectErasures
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenant.Value && r.SubjectId == subject.Value, ct)
            .ConfigureAwait(false);
        if (existing)
        {
            return false;
        }

        ctx.SubjectErasures.Add(new SubjectErasureRow
        {
            TenantId = tenant.Value,
            SubjectId = subject.Value,
            ErasedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ApprovingActorsJson = evidence is null
                ? null
                : JsonSerializer.Serialize(evidence.ApprovingActors.Select(a => a.Value).ToArray()),
            LegalBasis = evidence?.LegalBasis,
            ApprovedAtUnixMs = evidence?.ApprovedAt.ToUnixTimeMilliseconds(),
            // A mark without evidence (the base registry contract) has nothing to recover from: it completes
            // when its caller audits it, as before T-1048.
        });

        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // A concurrent mark raced us to the unique composite PK — the subject IS erased, this call just
            // wasn't the one that recorded it. Append-only + idempotent: report already-erased, never throw.
            return false;
        }
    }
}
