using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The durable EF/SQLite <see cref="ISubjectTombstoneStore"/> (ADR 0135 GDPR direction; the #1378 M-1
/// durable-erasure-store fix) — replaces the restart-volatile <see cref="InMemorySubjectTombstoneStore"/> on a
/// production node. Persists each pseudonymized <see cref="SubjectTombstone"/> as a write-once
/// <see cref="SubjectTombstoneRow"/> in the SAME SQLCipher <c>local-node.db</c> file as the erasure registry it
/// accompanies, so the compliance record SURVIVES a process restart (ADR 0068 §1.3).
/// </summary>
/// <remarks>
/// Write-once: a duplicate write for an already-tombstoned <c>(tenant, pseudonym)</c> preserves the first record
/// (the erasure service makes the erasure itself idempotent upstream). Carries no identifying plaintext — the
/// approving-actor chain + the deployer legal-basis reference are persisted as compliance metadata, never the
/// subject's identifying fields.
/// </remarks>
public sealed class NodeEfSubjectTombstoneStore : ISubjectTombstoneStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;

    /// <summary>Construct bound to the search context factory (the SQLCipher file the registry also lives in).</summary>
    public NodeEfSubjectTombstoneStore(IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(SubjectTombstone tombstone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tombstone);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Write-once: preserve the first tombstone if a duplicate erasure races to the same (tenant, pseudonym).
        var existing = await ctx.SubjectTombstones
            .AsNoTracking()
            .AnyAsync(t => t.TenantId == tombstone.TenantId.Value && t.Pseudonym == tombstone.Pseudonym, ct)
            .ConfigureAwait(false);
        if (existing)
        {
            return;
        }

        ctx.SubjectTombstones.Add(new SubjectTombstoneRow
        {
            TenantId = tombstone.TenantId.Value,
            Pseudonym = tombstone.Pseudonym,
            ErasedAtUnixMs = tombstone.ErasedAt.ToUnixTimeMilliseconds(),
            ApprovingActorsJson = JsonSerializer.Serialize(
                tombstone.ApprovingActors.Select(a => a.Value).ToArray(), Json),
            LegalBasis = tombstone.LegalBasis,
        });

        try
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent write recorded the tombstone first — write-once preserves it; this call is a no-op.
        }
    }

    /// <inheritdoc />
    public async ValueTask<SubjectTombstone?> FindAsync(TenantId tenant, string pseudonym, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pseudonym);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.SubjectTombstones
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenant.Value && t.Pseudonym == pseudonym, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToTombstone(row);
    }

    private static SubjectTombstone ToTombstone(SubjectTombstoneRow row)
    {
        var actorValues = JsonSerializer.Deserialize<string[]>(row.ApprovingActorsJson, Json) ?? Array.Empty<string>();
        var actors = actorValues.Select(v => new ActorId(v)).ToImmutableArray();
        return new SubjectTombstone(
            TenantId: TenantId.FromString(row.TenantId),
            Pseudonym: row.Pseudonym,
            ErasedAt: DateTimeOffset.FromUnixTimeMilliseconds(row.ErasedAtUnixMs),
            ApprovingActors: actors,
            LegalBasis: row.LegalBasis);
    }
}
