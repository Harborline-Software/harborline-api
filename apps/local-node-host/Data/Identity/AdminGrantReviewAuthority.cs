using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

public sealed record AdminGrantReviewResult(Guid AuditId, Guid CorrelationId, DateTimeOffset ReviewedAt);

internal sealed partial class AdminTeamAccessAuthority
{
    private static readonly AuditEventType GrantReviewRecorded = new("GrantReviewRecorded");

    public Task<AdminGrantReviewResult?> ReviewGrantAsync(
        string selectedSessionHandle, string tenantId, string grantId, AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default) => SerializedGrantActionAsync(
            () => ReviewGrantCoreAsync(selectedSessionHandle, tenantId, grantId, authority, cancellationToken), cancellationToken);

    private async Task<AdminGrantReviewResult?> ReviewGrantCoreAsync(
        string selectedSessionHandle, string tenantId, string grantId, AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorityTenant(tenantId, authority);
        var request = authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", grantId);
        var coverage = await _gate.DecideAsync(request, cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(coverage, cancellationToken).ConfigureAwait(false);
        coverage.RequireAllowed();
        var context = await ResolveAdminAsync(selectedSessionHandle, tenantId, authority.At, cancellationToken,
            requireGrantCoverage: true, request).ConfigureAwait(false);
        if (context is null || !Guid.TryParse(grantId, out var parsed)) return null;
        if (!string.Equals(context.Session.TenantPrincipalId, authority.Principal.Value, StringComparison.Ordinal))
            throw new ArgumentException("The selected-session principal does not match the write authority.", nameof(authority));
        var tenant = new TenantId(context.CanonicalTenantId);
        var target = new GrantId(parsed);
        if (await CorrelatedGrantReplayAsync(context.Decision, "grant-reviewed", GrantReviewRecorded, cancellationToken)
            .ConfigureAwait(false) is { } replay)
            return new(replay.AuditId, AuditCorrelation(replay)!.Value, replay.OccurredAt);
        var existing = await _grantStore.FindAsync(tenant, target, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Status == GrantStatus.Revoked) return null;
        var reviewed = await _grantRevocations.RecordReviewAsync(tenant, target, authority.At, authority.Principal,
            context.Decision, cancellationToken).ConfigureAwait(false);
        if (reviewed is null) return null;
        var correlation = authority.CorrelationId ?? Guid.NewGuid();
        var audit = await AppendGrantAuditAsync(tenant, target, context.Decision, GrantReviewRecorded,
            "grant-reviewed", correlation, null, cancellationToken).ConfigureAwait(false);
        return new(audit, correlation, reviewed.LastReviewedAt);
    }
}
