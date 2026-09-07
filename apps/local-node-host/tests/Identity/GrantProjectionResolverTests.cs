using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>ADR 0066 migration step 2 — project all authority records without migrating them.</summary>
public sealed class GrantProjectionResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("11111111-1111-1111-1111-111111111111");
    private static readonly ActorId Principal = new("principal-alice");
    private static readonly ActorId Granter = new("principal-granter");

    [Fact]
    public void Installation_access_grant_projects_field_by_field()
    {
        var row = InstallationRow(InstallationAccessGrantStatus.Revoked);

        var projected = GrantRecordProjections.Project(row);

        Assert.Equal(row.AccountId, projected.PrincipalId);
        Assert.Equal(PermissionCompositions.Member, projected.LegacyPermissions);
        Assert.Equal(ProjectedAuthorityScope.Installation, projected.Scope);
        Assert.Equal(row.IssuerId, projected.GrantedBy);
        Assert.Null(projected.Reason);
        Assert.Equal(row.CreatedAtUtc, projected.GrantedAt);
        Assert.Equal(row.CreatedAtUtc, projected.ValidFrom);
        Assert.Null(projected.ExpiresAt);
        Assert.Equal(row.UpdatedAtUtc, projected.RevokedAt);
        Assert.Null(projected.LastReviewedAt);
        Assert.Equal(GrantProjectionSource.InstallationAccessGrantRecord, projected.Source);
        Assert.Equal(row.GrantId, projected.SourceRecordId);
    }

    [Fact]
    public void Tenant_access_grant_projects_field_by_field()
    {
        var scope = ScopeExpression.Parse("/records/record-1");
        var grant = TenantGrant(
            scope,
            expiresAt: T0.AddDays(30),
            revokedAt: T0.AddDays(10));

        var projected = GrantRecordProjections.Project(grant);

        Assert.Equal(Principal.Value, projected.PrincipalId);
        Assert.Null(projected.LegacyPermissions);
        Assert.Equal(grant.Role, projected.Role);
        Assert.Equal(grant.Grant, projected.Grant);
        Assert.Equal(GrantProjectionScopeKind.Tenant, projected.Scope.Kind);
        Assert.Equal(Tenant, projected.Scope.TenantId);
        Assert.Same(scope, projected.Scope.RecordScope);
        Assert.Equal(Granter.Value, projected.GrantedBy);
        Assert.Equal(GrantReasonCodes.Manual, projected.Reason);
        Assert.Equal(grant.GrantedAt, projected.GrantedAt);
        Assert.Equal(grant.Validity.ValidFrom, projected.ValidFrom);
        Assert.Equal(grant.Validity.ValidTo, projected.ExpiresAt);
        Assert.Equal(grant.Revocation?.RevokedAt, projected.RevokedAt);
        Assert.Equal(grant.LastReviewedAt, projected.LastReviewedAt);
        Assert.Equal(GrantProjectionSource.AccessGrant, projected.Source);
        Assert.Equal(grant.GrantId.ToString(), projected.SourceRecordId);
    }

    [Fact]
    public void Team_membership_permissions_project_field_by_field()
    {
        var membership = Membership(PermissionCompositions.Admin);

        var projected = GrantRecordProjections.Project(Principal, membership);

        Assert.Equal(Principal.Value, projected.PrincipalId);
        Assert.Equal(PermissionCompositions.Admin, projected.LegacyPermissions);
        Assert.Equal(GrantProjectionScopeKind.Tenant, projected.Scope.Kind);
        Assert.Equal(Tenant, projected.Scope.TenantId);
        Assert.Equal("/", projected.Scope.RecordScope!.Value);
        Assert.Equal(Granter.Value, projected.GrantedBy);
        Assert.Null(projected.Reason);
        Assert.Equal(T0, projected.GrantedAt);
        Assert.Equal(T0, projected.ValidFrom);
        Assert.Null(projected.ExpiresAt);
        Assert.Null(projected.RevokedAt);
        Assert.Null(projected.LastReviewedAt);
        Assert.Equal(GrantProjectionSource.TeamMembershipPermissions, projected.Source);
        Assert.Equal(Tenant.Value, projected.SourceRecordId);
    }

    [Fact]
    public async Task Principal_rows_from_all_three_sources_are_returned_with_provenance()
    {
        var installation = new FixedInstallationReader(InstallationRow());
        var tenant = TestInMemoryAuthorizationStores.GrantStore();
        await tenant.SaveAsync(
            Tenant,
            TenantGrant(ScopeExpression.Parse("/")),
            expectedOwnerVersion: 0);
        var memberships = new InMemoryTeamRegistry();
        await memberships.AddMembershipAsync(Principal, Membership(PermissionCompositions.Member));
        var logger = new CollectingLogger();
        var resolver = new GrantProjectionResolver(installation, tenant, memberships, logger);

        var result = await resolver.ResolveAsync(Principal.Value, Tenant);

        Assert.Equal(3, result.Grants.Count);
        Assert.Single(result.Disagreements);
        Assert.Single(logger.Entries);
        Assert.Equal(
            [
                GrantProjectionSource.InstallationAccessGrantRecord,
                GrantProjectionSource.AccessGrant,
                GrantProjectionSource.TeamMembershipPermissions,
            ],
            result.Grants.Select(grant => grant.Source).ToArray());
        Assert.All(result.Grants, grant => Assert.False(string.IsNullOrWhiteSpace(grant.SourceRecordId)));
    }

    [Fact]
    public async Task Disagreement_is_surfaced_without_merging_and_logs_warning()
    {
        var tenant = TestInMemoryAuthorizationStores.GrantStore();
        await tenant.SaveAsync(
            Tenant,
            TenantGrant(ScopeExpression.Parse("/")),
            expectedOwnerVersion: 0);
        var memberships = new InMemoryTeamRegistry();
        await memberships.AddMembershipAsync(Principal, Membership(PermissionCompositions.Admin));
        var logger = new CollectingLogger();
        var resolver = new GrantProjectionResolver(
            new FixedInstallationReader(), tenant, memberships, logger);

        var result = await resolver.ResolveAsync(Principal.Value, Tenant);

        Assert.Equal(2, result.Grants.Count);
        var disagreement = Assert.Single(result.Disagreements);
        Assert.Equal(Principal.Value, disagreement.PrincipalId);
        Assert.Equal(GrantProjectionDisagreementKind.PermissionSet, disagreement.Kind);
        Assert.Equal(GrantProjectionSource.AccessGrant, disagreement.FirstSource);
        Assert.Equal(GrantProjectionSource.TeamMembershipPermissions, disagreement.SecondSource);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ADR 0066 grant projection disagreement", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_stores_return_empty_without_throwing()
    {
        var resolver = new GrantProjectionResolver(
            new FixedInstallationReader(),
            TestInMemoryAuthorizationStores.GrantStore(),
            new InMemoryTeamRegistry(),
            new CollectingLogger());

        var result = await resolver.ResolveAsync(Principal.Value, Tenant);

        Assert.Empty(result.Grants);
        Assert.Empty(result.Disagreements);
    }

    private static InstallationAccessGrantRecord InstallationRow(
        InstallationAccessGrantStatus status = InstallationAccessGrantStatus.Active) =>
        new()
        {
            GrantId = "installation-grant-1",
            AccountId = Principal.Value,
            PermissionsJson = JsonSerializer.Serialize(PermissionCompositions.Member.Permissions),
            Status = status,
            IssuerKind = "installer",
            IssuerId = Granter.Value,
            AuthorizationEpoch = 1,
            OwnerVersion = 1,
            AuditCorrelationId = "ticket-200",
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0.AddDays(1),
        };

    private static AccessGrant TenantGrant(
        ScopeExpression scope,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null) =>
        new(
            new GrantId(Guid.Parse("22222222-2222-2222-2222-222222222222")), Tenant, Principal,
            AccessGrantAuthorizationSeed.MemberRole, scope, GrantResidency.Cache,
            new GrantValidity(T0, expiresAt), GranterKind.Person, Granter, T0.AddMinutes(-5),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Granter),
            T0, revokedAt is null ? GrantStatus.Active : GrantStatus.Revoked,
            revokedAt is null ? null : new GrantRevocation(Granter, revokedAt.Value,
                new GrantReason(GrantReasonCodes.RevocationOffboarding)));

    private static TeamMembership Membership(PermissionSet permissions) =>
        new(
            TeamId: Guid.Parse(Tenant.Value),
            DisplayName: "Test team",
            RoleDisplayName: "Member",
            SubkeyFingerprint: new KeyFingerprint("test-fingerprint"),
            Role: TeamRole.Member,
            Permissions: permissions,
            MemberPublicKey: "member-public-key",
            AdmissionSignature: new AdmissionSignature(
                AdmittedByPublicKey: "granter-public-key",
                AdmittedByPartyId: Granter.Value,
                IssuedAt: T0,
                Nonce: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Signature: "signature",
                IsGenesis: false));

    private sealed class FixedInstallationReader(params InstallationAccessGrantRecord[] rows)
        : IInstallationAccessGrantProjectionReader
    {
        public Task<IReadOnlyList<InstallationAccessGrantRecord>> FindByPrincipalAsync(
            string principalId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstallationAccessGrantRecord>>(
                rows.Where(row => string.Equals(row.AccountId, principalId, StringComparison.Ordinal)).ToArray());
    }

    private sealed class CollectingLogger : ILogger<GrantProjectionResolver>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
