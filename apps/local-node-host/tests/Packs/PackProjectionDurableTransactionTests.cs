using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackProjectionDurableTransactionTests
{
    private static readonly TenantId Tenant = new("projection-atomicity");
    private const string Pack = "atomicity.fixture";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pack_pointer_and_authorization_lifecycle_share_one_encrypted_commit(bool commit)
    {
        await using var database = await SearchTestStore.CreateAsync();
        await using (var packs = database.CreatePacksContext()) await packs.Database.MigrateAsync();
        var store = new DurablePackInstallStore(database.PacksFactory);
        Install(store, "1.0.0");
        store.Activate(Tenant, Pack, "1.0.0");
        Install(store, "1.1.0");
        var roles = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var configuration = new NodeEfAuthorizationConfigurationStore(database.Factory, roles);
        var writer = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(roles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), TestInMemoryAuthorizationStores.GrantStore());
        var operation = AuthorizationOperation.Parse("records:read");
        var definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.NewGuid()), Pack, 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/records/m6")), RoleBindingSet.From([RoleReference.Administrator]));
        await writer.WriteAsync(new InstallAuthorizationDefinition(definition, Tenant), TestAuthorization.Write(Tenant));
        var before = JsonSerializer.Serialize(await configuration.ListAsync(Tenant));
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(store);
            transaction.Enlist(writer);
            store.Activate(Tenant, Pack, "1.1.0");
            await writer.WriteAsync(new ReplaceAuthorizationDefinition(
                definition with { Revision = 2, OfferedRoles = RoleBindingSet.Empty }, Tenant), TestAuthorization.Write(Tenant));
            Assert.NotEqual(before, JsonSerializer.Serialize(await configuration.ListAsync(Tenant)));
            if (commit) transaction.Commit();
        }
        await using var restart = SearchTestStore.Reopen(database);
        var reopenedPacks = new DurablePackInstallStore(restart.PacksFactory);
        var reopenedConfiguration = new NodeEfAuthorizationConfigurationStore(restart.Factory, roles);
        Assert.Equal(commit ? "1.1.0" : "1.0.0", reopenedPacks.GetActive(Tenant, Pack)!.Version);
        var after = JsonSerializer.Serialize(await reopenedConfiguration.ListAsync(Tenant));
        if (commit) Assert.NotEqual(before, after);
        else Assert.Equal(before, after);
        Assert.Equal(after, JsonSerializer.Serialize(await configuration.ListAsync(Tenant)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_pointer_ownership_and_admission_restart_at_one_commit_boundary(bool commit)
    {
        await using var database = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(database.Factory);
        Install(store, "1.0.0");
        store.Activate(Tenant, Pack, "1.0.0");
        Install(store, "1.1.0");
        var before = JsonSerializer.Serialize(store.ListInstalled(Tenant));
        var admission = new PackProjectionAdmission(Guid.NewGuid(), Pack, "1.1.0", Tenant,
            new ActorId("operator"), DateTimeOffset.UnixEpoch.AddDays(1), ["grant", "definition"], false);
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(store);
            store.RecordKeyOwnership(Tenant, "new-owner", Pack);
            ((IPackProjectionAdmissionStore)store).ActivateAndRecordProjectionAdmission(Tenant, Pack, "1.1.0", admission);
            Assert.Equal("1.1.0", store.GetActive(Tenant, Pack)!.Version);
            ((IPackProjectionAdmissionStore)store).MarkProjectionCompleted(admission.AdmissionId);
            if (commit) transaction.Commit();
        }
        await using var restart = PacksTestStore.Reopen(database);
        var reopened = new DurablePackInstallStore(restart.Factory);
        Assert.Equal(commit ? "1.1.0" : "1.0.0", reopened.GetActive(Tenant, Pack)!.Version);
        Assert.Equal(commit, reopened.GetKeyOwnership(Tenant).ContainsKey("new-owner"));
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        if (!commit) Assert.Equal(before, JsonSerializer.Serialize(reopened.ListInstalled(Tenant)));
        // The same instance must leave the disposed transaction and return to ordinary operations.
        Assert.Equal(reopened.GetActive(Tenant, Pack)!.Version, store.GetActive(Tenant, Pack)!.Version);
    }

    [Fact]
    public async Task Different_database_cannot_join_a_pack_activation()
    {
        await using var first = await PacksTestStore.CreateAsync();
        await using var second = await PacksTestStore.CreateAsync();
        var firstStore = new DurablePackInstallStore(first.Factory);
        var secondStore = new DurablePackInstallStore(second.Factory);
        Install(firstStore, "1.0.0");
        Install(secondStore, "1.0.0");
        using var transaction = new PackProjectionTransaction();
        transaction.Enlist(firstStore);
        transaction.Enlist(secondStore);
        Assert.Throws<InvalidOperationException>(() => secondStore.GetActive(Tenant, Pack));
    }

    private static void Install(DurablePackInstallStore store, string version)
    {
        var pack = new InstalledPack(Pack, version, PackScopeTier.Horizontal, PackLifecycleState.Draft,
            [], new Dictionary<string, int>(), DateTimeOffset.UnixEpoch,
            PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster, []);
        store.Commit(new PackInstallTransaction(Tenant, pack,
            new PackInstallWatermark(Pack, version, new Dictionary<string, int>()), []));
    }
}
