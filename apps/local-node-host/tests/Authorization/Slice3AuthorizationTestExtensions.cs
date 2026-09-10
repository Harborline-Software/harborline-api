using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

internal static class Slice3AuthorizationTestExtensions
{
    internal static Task<AccessGrant> IssueAsync(
        this InitialGrantIssuanceService service,
        AdmissionCompleted admission,
        CancellationToken ct = default) =>
        service.IssueAsync(
            admission,
            TestAuthorization.Write(
                admission.TenantId,
                admission.InviterPrincipal.Value,
                service.CurrentInstant),
            ct);

    internal static Task<InitialGrantIssuanceResult> IssueWithEpochAsync(
        this InitialGrantIssuanceService service,
        AdmissionCompleted admission,
        CancellationToken ct = default) =>
        service.IssueWithEpochAsync(
            admission,
            TestAuthorization.Write(
                admission.TenantId,
                admission.InviterPrincipal.Value,
                service.CurrentInstant),
            ct);

    internal static Task<AccountSetupInvitationIssueResult?> IssueAsync(
        this AccountSetupInvitationIssuer issuer,
        string selectedSessionHandle,
        AccountSetupInvitationIssueRequest request,
        CancellationToken ct = default) =>
        issuer.IssueAsync(
            selectedSessionHandle,
            request,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(request.TenantId).ToString("D")),
                "principal-admin",
                issuer.CurrentInstant),
            ct);

    internal static Task<RecoveryInvitationIssueResult?> IssueAsync(
        this RecoveryInvitationIssuer issuer,
        string selectedSessionHandle,
        RecoveryInvitationIssueRequest request,
        CancellationToken ct = default) =>
        issuer.IssueAsync(
            selectedSessionHandle,
            request,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(request.TenantId).ToString("D")), "principal-admin"),
            ct);

    internal static Task<AdminIssuedInvitation?> IssueInvitationAsync(
        this IAdminTeamAccessAuthority authority,
        string selectedSessionHandle,
        string tenantId,
        IReadOnlyCollection<string> requestedPermissions,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var at = authority is AdminTeamAccessAuthority admin ? admin.CurrentInstant : TestAuthorization.At;
        return authority.IssueInvitationAsync(
            selectedSessionHandle,
            tenantId,
            requestedPermissions,
            idempotencyKey,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(tenantId).ToString("D")), "principal-admin", at),
            ct);
    }

    internal static Task<AdminRevokeMemberResult?> RevokeMemberGrantAsync(
        this IAdminTeamAccessAuthority authority,
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        string? successorPrincipalId = null,
        CancellationToken ct = default)
    {
        var at = authority is AdminTeamAccessAuthority admin ? admin.CurrentInstant : TestAuthorization.At;
        return authority.RevokeMemberGrantAsync(
            selectedSessionHandle,
            tenantId,
            grantId,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(tenantId).ToString("D")), "principal-admin", at),
            successorPrincipalId,
            ct);
    }

    internal static Task<AdminNarrowMemberGrantResult?> NarrowMemberGrantAsync(
        this IAdminTeamAccessAuthority authority,
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        IReadOnlyCollection<string> narrowedPermissions,
        CancellationToken ct = default)
    {
        var at = authority is AdminTeamAccessAuthority admin ? admin.CurrentInstant : TestAuthorization.At;
        return authority.NarrowMemberGrantAsync(
            selectedSessionHandle,
            tenantId,
            grantId,
            narrowedPermissions,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(tenantId).ToString("D")), "principal-admin", at),
            ct);
    }

    internal static Task<AdminUpdateMemberPermissionsResult?> UpdateMemberPermissionsAsync(
        this IAdminTeamAccessAuthority authority,
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        IReadOnlyCollection<string> requestedPermissions,
        CancellationToken ct = default)
    {
        var at = authority is AdminTeamAccessAuthority admin ? admin.CurrentInstant : TestAuthorization.At;
        return authority.UpdateMemberPermissionsAsync(
            selectedSessionHandle,
            tenantId,
            grantId,
            requestedPermissions,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(tenantId).ToString("D")), "principal-admin", at),
            ct);
    }

    internal static Task<AdministratorAuthorityResult> AppendRemovalAsync(
        this NodeAdministratorAuthority authority,
        string teamId,
        string partyId,
        AdministratorAuthorityEvent eventType,
        string reasonCode,
        CancellationToken ct = default) =>
        authority.AppendRemovalAsync(
            teamId,
            partyId,
            eventType,
            reasonCode,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(teamId).ToString("D")), partyId, authority.CurrentInstant),
            ct);

    internal static Task<AdministratorAuthorityResult> SetExpiryAsync(
        this NodeAdministratorAuthority authority,
        string teamId,
        string partyId,
        DateTimeOffset? expiresAtUtc,
        string reasonCode,
        CancellationToken ct = default) =>
        authority.SetExpiryAsync(
            teamId,
            partyId,
            expiresAtUtc,
            reasonCode,
            TestAuthorization.Write(
                new TenantId(Guid.Parse(teamId).ToString("D")), partyId, authority.CurrentInstant),
            ct);

    internal static Task<bool> IndexNodeAsync(
        this NodeSearchIndexer indexer,
        SearchNodeRow node,
        CancellationToken ct = default) =>
        indexer.IndexNodeAsync(
            node,
            TestAuthorization.AllowedDecision(new TenantId(node.TenantId), node.RecordId),
            ct);

    internal static Task IndexEdgesAsync(
        this NodeSearchIndexer indexer,
        string sourceRecordId,
        IReadOnlyList<SearchEdgeRow> edges,
        CancellationToken ct = default)
    {
        var tenant = edges.Count == 0 ? "tenant-test" : edges[0].TenantId;
        return indexer.IndexEdgesAsync(
            tenant,
            sourceRecordId,
            edges,
            TestAuthorization.AllowedDecision(new TenantId(tenant), sourceRecordId),
            ct);
    }

    internal static Task OnResidencyChangedAsync(
        this NodeSearchIndexer indexer,
        string recordId,
        GrantResidency residency,
        CancellationToken ct = default) =>
        indexer.OnResidencyChangedAsync(
            "tenant-test",
            recordId,
            residency,
            TestAuthorization.AllowedDecision(new TenantId("tenant-test"), recordId),
            ct);

    internal static Task DeleteRecordAsync(
        this NodeSearchIndexer indexer,
        string recordId,
        CancellationToken ct = default) =>
        indexer.DeleteRecordAsync(
            "tenant-test",
            recordId,
            TestAuthorization.AllowedDecision(new TenantId("tenant-test"), recordId),
            ct);

    internal static Task<bool> IndexRecordAsync(
        this NodeVecIndexer indexer,
        string recordId,
        string tenantId,
        string? subjectId,
        string text,
        SearchResidency residency,
        CancellationToken ct = default) =>
        indexer.IndexRecordAsync(
            recordId,
            tenantId,
            subjectId,
            text,
            residency,
            TestAuthorization.AllowedDecision(new TenantId(tenantId), recordId),
            ct);

    internal static Task IndexArtifactAsync(
        this NodeVecIndexer indexer,
        KgEmbeddingArtifact artifact,
        SearchResidency residency,
        CancellationToken ct = default) =>
        indexer.IndexArtifactAsync(
            artifact,
            residency,
            TestAuthorization.AllowedDecision(new TenantId(artifact.TenantId), artifact.RecordId),
            ct);

    internal static Task DeleteRecordAsync(
        this NodeVecIndexer indexer,
        string recordId,
        CancellationToken ct = default) =>
        indexer.DeleteRecordAsync(
            "tenant-test",
            recordId,
            TestAuthorization.AllowedDecision(new TenantId("tenant-test"), recordId),
            ct);

    internal static Task<PostResult> PostAsync(
        this JournalPostingService service,
        JournalEntry entry,
        CancellationToken ct = default) =>
        service.PostAsync(
            entry,
            TestAuthorization.Write(entry.TenantId, at: entry.CreatedAtUtc.Value),
            ct);
}

internal sealed class AlwaysPostableAccountResolver : IAccountResolver
{
    public Task<GLAccount?> GetAsync(GLAccountId id, CancellationToken cancellationToken = default) =>
        Task.FromResult<GLAccount?>(new GLAccount(id, id.Value, id.Value, GLAccountType.Asset));

    public Task<IReadOnlyList<GLAccount>> EnumerateForChartAsync(
        ChartOfAccountsId chartId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GLAccount>>([]);
}

internal sealed class NoopRosterMemberRevocationAuthority : INodeRosterMemberRevocationAuthority
{
    public ValueTask<CompromisedDeviceRevocation?> RevokeAsync(
        TenantId tenant,
        string decisionTargetId,
        string revokedPartyId,
        string revokedByPartyId,
        string reason,
        string? correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<CompromisedDeviceRevocation?>(null);
}
