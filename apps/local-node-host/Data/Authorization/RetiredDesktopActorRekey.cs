using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>
/// Ticket 294 slice 3b — the one-time rekey of grant rows written before the desktop actor became the founder's
/// canonical principal. Grant rows are unsigned, so rewriting their subject is honest (unlike the signed roster
/// admissions slice 2b refused). Each row moves to the node operator, gets a new owner version, and advances the
/// operator's authorization epoch and the tenant version so the closure is rebuilt; the retired epoch rows are
/// deleted. Provenance fields (granted-by, approver, revoked-by) are history and stay as written.
/// </summary>
internal static class RetiredDesktopActorRekey
{
    /// <summary>The event type a rekeyed grant subject is recorded under.</summary>
    internal static readonly AuditEventType RekeyedEventType = new("AuthorizationGrantSubjectRekeyed");

    /// <summary>Rekeys every retired row to <paramref name="nodeOperator"/>; returns how many grants moved.</summary>
    /// <exception cref="InvalidOperationException">
    /// Retired rows exist and there is no node operator (<see cref="GenesisStartupMessages.DesktopActorUnresolved"/>).
    /// </exception>
    internal static async Task<int> RunAsync(
        IDbContextFactory<NodeLocalSearchDbContext> factory, ActorId? nodeOperator, DateTimeOffset at, CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var grants = await ctx.Grants
                .Where(row => row.SubjectId == NodeOperatorIdentity.RetiredDesktopActor)
                .ToListAsync(ct).ConfigureAwait(false);
            var epochs = await ctx.GrantAuthorizationEpochs
                .Where(row => row.PrincipalId == NodeOperatorIdentity.RetiredDesktopActor)
                .ToListAsync(ct).ConfigureAwait(false);
            if (grants.Count == 0 && epochs.Count == 0) return 0;
            if (nodeOperator is not { } holder)
                throw new InvalidOperationException(GenesisStartupMessages.DesktopActorUnresolved);

            ctx.GrantAuthorizationEpochs.RemoveRange(epochs);
            foreach (var row in grants)
            {
                row.SubjectId = holder.Value;
                row.OwnerVersion = checked(row.OwnerVersion + 1);
                // DES-0029 ck-6: moving a grant to another subject is an authorization change; its audit commits
                // in this fence with it.
                NodeAuditOutbox.StageSystem(ctx, RekeyedEventType, new TenantId(row.TenantId), at, holder,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["grantId"] = row.GrantId,
                        ["fromSubject"] = NodeOperatorIdentity.RetiredDesktopActor,
                        ["toSubject"] = holder.Value,
                    });
                await NodeEfGrantStore.AdvanceEpochAsync(ctx, new TenantId(row.TenantId), holder, ct)
                    .ConfigureAwait(false);
            }
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return grants.Count;
        }, ct).ConfigureAwait(false);
    }
}
