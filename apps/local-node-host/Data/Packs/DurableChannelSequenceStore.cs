using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// The durable persistence home of the update-feed F2 anti-rollback high-water (design note §7.2 / F2): the
/// highest channel-index <c>sequence</c> this node has ever verified, per (tenant, channel). The node feed
/// client reads it as the refuse-lower floor (combined with the build-time first-contact floor pinned beside
/// the channel root) and advances it ONLY after a whole-tree verify succeeds — so a rolled-back index is
/// refused before it can advance anything, and the high-water SURVIVES a node restart / restore-from-backup.
/// </summary>
public interface IChannelSequenceStore
{
    /// <summary>The highest channel <c>sequence</c> verified for (<paramref name="tenant"/>,
    /// <paramref name="channelId"/>), or <c>0</c> if this node has never verified that channel (first contact —
    /// the build-time floor is the backstop there).</summary>
    long GetHighWater(TenantId tenant, string channelId);

    /// <summary>Monotonically advance the high-water for (<paramref name="tenant"/>,
    /// <paramref name="channelId"/>) to <paramref name="sequence"/>. A value at-or-below the persisted
    /// high-water is a NO-OP (the high-water only ever moves up). Call ONLY after a successful verify.</summary>
    void AdvanceHighWater(TenantId tenant, string channelId, long sequence);
}

/// <summary>
/// The SQLCipher-backed <see cref="IChannelSequenceStore"/> over <see cref="NodeLocalPacksDbContext"/> (the same
/// encrypted <c>local-node.db</c> file + single-gate model as <see cref="DurablePackInstallStore"/>). Node-
/// exclusive install bookkeeping — never synced, never a shared entity module (F6: the channel-table CONFIG may
/// sync, but this freshness high-water and the trust-root pin are per-node only).
/// </summary>
public sealed class DurableChannelSequenceStore : IChannelSequenceStore
{
    private readonly object _gate = new();
    private readonly IDbContextFactory<NodeLocalPacksDbContext> _factory;

    /// <summary>Construct over the SQLCipher-keyed packs DbContext factory (registered by
    /// <c>AddSqlCipherLocalNodeDbContext</c>).</summary>
    public DurableChannelSequenceStore(IDbContextFactory<NodeLocalPacksDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public long GetHighWater(TenantId tenant, string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.FeedChannelSequences.AsNoTracking()
                .FirstOrDefault(r => r.Tenant == t && r.ChannelId == channelId);
            return row?.HighWaterSequence ?? 0L;
        }
    }

    /// <inheritdoc />
    public void AdvanceHighWater(TenantId tenant, string channelId, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.FeedChannelSequences.Find(t, channelId);
            if (row is null)
            {
                ctx.FeedChannelSequences.Add(new FeedChannelSequenceRow
                {
                    Tenant = t,
                    ChannelId = channelId,
                    HighWaterSequence = sequence,
                });
            }
            else if (sequence > row.HighWaterSequence)
            {
                // Monotonic: only ever moves UP. A lower/equal sequence is a no-op (never lowers the fence).
                row.HighWaterSequence = sequence;
            }
            else
            {
                return; // no change — avoid a needless write.
            }

            ctx.SaveChanges();
        }
    }
}
