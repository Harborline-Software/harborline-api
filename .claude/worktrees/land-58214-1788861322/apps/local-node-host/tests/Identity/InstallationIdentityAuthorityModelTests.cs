using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class InstallationIdentityAuthorityModelTests
{
    private static readonly string[] ForbiddenBusinessAuthorityNames =
    [
        "TenantId",
        "TeamId",
        "PartyId",
        "PrincipalId",
        "RoleId",
        "BusinessGrantId",
        "BusinessPermissions",
    ];

    [Fact]
    public void Account_Is_Installation_Scoped_And_Username_Is_Globally_Unique()
    {
        using var context = CreateContext();
        var account = context.Model.FindEntityType(typeof(InstallationAccountRecord));

        Assert.NotNull(account);
        Assert.Equal([nameof(InstallationAccountRecord.AccountId)],
            account.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.DoesNotContain(
            account.GetProperties(),
            property => ForbiddenBusinessAuthorityNames.Contains(property.Name, StringComparer.Ordinal));

        var usernameIndex = Assert.Single(
            account.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(InstallationAccountRecord.NormalizedUsername)]));
        Assert.True(usernameIndex.IsUnique);
    }

    [Fact]
    public void Installation_Sessions_And_Tenant_Membership_Rows_Are_Not_In_This_Slice()
    {
        using var context = CreateContext();
        var entityTypes = context.Model.GetEntityTypes().Select(entity => entity.ClrType.Name).ToArray();

        Assert.DoesNotContain("NodeWebMembershipRow", entityTypes, StringComparer.Ordinal);
        Assert.DoesNotContain("WebUserSessionRow", entityTypes, StringComparer.Ordinal);
        Assert.DoesNotContain("WebInstallationSessionRow", entityTypes, StringComparer.Ordinal);
        Assert.DoesNotContain("WebAccountAccessChallengeRow", entityTypes, StringComparer.Ordinal);
        Assert.Contains(nameof(InstallationIdentityCoordinatorRecord), entityTypes, StringComparer.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "INV-01A")]
    public void AccountSetup_Invitation_Is_DigestOnly_PurposeBound_And_Does_Not_Reserve_Identity()
    {
        using var context = CreateContext();
        var invitation = RequiredEntity<AccountSetupInvitationRecord>(context.Model);

        Assert.Equal(
            [nameof(AccountSetupInvitationRecord.InvitationId)],
            invitation.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(
            nameof(AccountSetupInvitationRecord.TokenDigest),
            invitation.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(AccountSetupInvitationRecord.Purpose),
            invitation.GetProperties().Select(property => property.Name));
        Assert.DoesNotContain(
            invitation.GetProperties(),
            property => property.Name.Contains("Username", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("AccountId", StringComparison.Ordinal) ||
                property.Name.Equals("MembershipId", StringComparison.Ordinal));
        Assert.True(Assert.Single(
            invitation.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AccountSetupInvitationRecord.TokenDigest)])).IsUnique);
    }

    [Fact]
    public void Coordinator_Owns_Only_Cross_Store_Decision_And_Receipt_Evidence()
    {
        using var context = CreateContext();
        var coordinator = RequiredEntity<InstallationIdentityCoordinatorRecord>(context.Model);

        Assert.Equal(
            [nameof(InstallationIdentityCoordinatorRecord.CorrelationId)],
            coordinator.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.ExpectedAccountOwnerVersion),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.ExpectedAccountSecurityVersion),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.ExpectedActorOwnerVersion),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.ExpectedActorSecurityVersion),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.PayloadSchemaVersion),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.ActorAccountId),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.AuthorityEvidenceDigest),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.Contains(
            nameof(InstallationIdentityCoordinatorRecord.FinalReceiptsJson),
            coordinator.GetProperties().Select(property => property.Name));
        Assert.DoesNotContain(
            coordinator.GetProperties(),
            property => property.Name.Contains("Party", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Principal", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("BusinessGrant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Installation_Grant_Is_Unambiguous_Per_Account()
    {
        using var context = CreateContext();
        var grant = RequiredEntity<InstallationAccessGrantRecord>(context.Model);

        Assert.Equal(
            [nameof(InstallationAccessGrantRecord.AccountId)],
            grant.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.NotNull(grant.FindKey(
            [grant.FindProperty(nameof(InstallationAccessGrantRecord.GrantId))!]));
    }

    [Fact]
    public void Root_And_Audit_Evidence_Are_Bound_To_Stable_Installation_Identity()
    {
        using var context = CreateContext();
        var identity = RequiredEntity<InstallationIdentityRecord>(context.Model);
        var rootEpoch = RequiredEntity<InstallationRootKeyEpochRecord>(context.Model);
        var auditHead = RequiredEntity<InstallationAuditHeadRecord>(context.Model);
        var auditEnvelope = RequiredEntity<InstallationAuditEnvelopeRecord>(context.Model);

        Assert.NotNull(identity.FindKey(
            [identity.FindProperty(nameof(InstallationIdentityRecord.InstallationIdentityId))!]));
        Assert.Equal(
            [nameof(InstallationRootKeyEpochRecord.InstallationIdentityId),
                nameof(InstallationRootKeyEpochRecord.EpochNumber)],
            rootEpoch.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(InstallationAuditHeadRecord.InstallationIdentityId)],
            auditHead.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(InstallationAuditEnvelopeRecord.InstallationIdentityId),
                nameof(InstallationAuditEnvelopeRecord.Sequence)],
            auditEnvelope.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.True(Assert.Single(
            auditEnvelope.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(InstallationAuditEnvelopeRecord.CorrelationId)])).IsUnique);
        Assert.DoesNotContain(
            auditEnvelope.GetProperties(),
            property => property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Context_Is_Catalog_Owned_And_Uses_A_Dedicated_Migration_History()
    {
        var descriptor = LocalNodeExclusiveEfContextCatalog.For<NodeLocalInstallationIdentityDbContext>();

        Assert.Equal("local-node.ef.installation-identity", descriptor.ContextKey);
        Assert.Equal(LocalNodeExclusiveMigrationOwner.EncryptionGuard, descriptor.Owner);
        Assert.Equal(
            NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName,
            descriptor.MigrationsHistoryTable);
        Assert.NotEqual("__EFMigrationsHistory", descriptor.MigrationsHistoryTable);
        Assert.Equal([
                "20260713214619_InstallationIdentityInitial",
                "20260713233947_InstallationIdentityCoordination",
                "20260714103750_InstallationIdentityCutoverRecords",
                "20260718132100_LegacyRenameCheckpointBinding",
                "20260718161500_AccountSetupInvitations",
                "20260723050000_RecoveryInvitations",
                "20260728043828_LegacyBearerCutoverEvidence",
                "20260902140000_BootstrapClaimMarker",
            ],
            descriptor.CompiledMigrations.Select(migration => migration.MigrationId));
    }

    private static IEntityType RequiredEntity<T>(IModel model) where T : class =>
        model.FindEntityType(typeof(T))
        ?? throw new InvalidOperationException($"Missing entity model for {typeof(T).Name}.");

    private static NodeLocalInstallationIdentityDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalInstallationIdentityDbContext(options);
    }
}
