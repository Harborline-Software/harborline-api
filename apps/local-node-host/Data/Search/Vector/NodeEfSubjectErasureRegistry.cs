using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

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
/// </remarks>
public sealed class NodeEfSubjectErasureRegistry : ISubjectErasureRegistry
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
    public async ValueTask<bool> MarkErasedAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
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
