using System.Reflection;
using System.Runtime.CompilerServices;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationDefinitionBindingTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly ActorId Actor = new("actor-a");
    private static readonly RoleReference Author = new(RoleVocabularies.Domain, "author");
    private static readonly RoleReference Reviewer = new(RoleVocabularies.Domain, "reviewer");

    [Fact]
    public async Task KernelWritePipeline_ExecutesSixStagesInOrder()
    {
        var (writer, _, _) = CreateWriter();

        var result = await writer.WriteAsync(new InstallAuthorizationDefinition(ReadDefinition()));

        Assert.Equal(
            ["authorize", "bind", "mutate", "validate", "commit", "react"],
            result.Stages);
    }

    [Fact]
    public async Task KernelWritePipeline_DoesNotCommitWhenValidateRefuses()
    {
        var (writer, _, reader) = CreateWriter();
        var invalid = ReadDefinition() with
        {
            Operation = AuthorizationOperation.Parse("unknown:read"),
            Atom = PermissionAtom.Parse("unknown:read@/"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(invalid)).AsTask());

        Assert.Empty(await reader.DefinitionsForRoleAsync(Tenant, RoleReference.Administrator));
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_InstallsKnownOperationAndOfferedRoles()
    {
        var services = new ServiceCollection().AddAccessGrantModule();
        services.AddSingleton(TestAuthorization.AllowGate());
        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<AuthorizationDefinitionWriter>();
        var reader = provider.GetRequiredService<IAuthorizationDefinitionReader>();

        var result = await writer.WriteAsync(new InstallAuthorizationDefinition(ReadDefinition()));

        Assert.Equal(ReadDefinition(), result.Definition);
        Assert.Contains(
            await reader.DefinitionsForRoleAsync(Tenant, RoleReference.Administrator),
            definition => definition.DefinitionId == ReadDefinition().DefinitionId);
        Assert.IsType<InMemoryAuthorizationConfigurationStore>(
            provider.GetRequiredService<IAuthorizationConfigurationStore>());
        Assert.NotNull(provider.GetRequiredService<AuthorizationDefinitionAdmission>());
        Assert.NotNull(provider.GetRequiredService<AuthorizationCapabilityBindingAdmission>());
    }

    [Fact]
    public async Task AccessGrantModuleAndLifecycleHandlersResolveIssuanceWithoutHostRegistrations()
    {
        var services = new ServiceCollection()
            .AddAccessGrantModule()
            .AddAccessGrantLifecycleHandlers(
                _ => new StubIssuanceContext(),
                _ => new StubRevocationContext());
        await using var provider = services.BuildServiceProvider();

        var handlers = provider.GetServices<IWorkflowStepHandler>();

        Assert.Contains(handlers, handler => handler is GrantIssuanceHandler);
        Assert.IsType<DefinitionJoinedAuthorizationReader>(
            provider.GetRequiredService<IAuthorizationClosureReader>());
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_RefusesUnknownCodeOperation()
    {
        var (writer, _, _) = CreateWriter();
        var definition = ReadDefinition() with
        {
            Operation = AuthorizationOperation.Parse("records:inspect"),
            Atom = PermissionAtom.Parse("records:inspect@/"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(definition)).AsTask());
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_RefusesAtomOperationMismatch()
    {
        var (writer, _, _) = CreateWriter();
        var definition = ReadDefinition() with
        {
            Atom = PermissionAtom.Parse("records:write@/"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(definition)).AsTask());
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_RefusesUnknownOrWronglyOwnedRole()
    {
        var unknownRole = new RoleReference(RoleVocabularies.Domain, "unknown");
        var (writer, _, _) = CreateWriter();
        var unknown = WriteDefinition() with { OfferedRoles = RoleBindingSet.Of(unknownRole) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(unknown)).AsTask());

        var wrongOwner = WriteDefinition() with
        {
            PublisherPackageId = "package-b",
            OfferedRoles = RoleBindingSet.Of(Author),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(wrongOwner)).AsTask());

        var auditorWrite = WriteDefinition() with
        {
            OfferedRoles = RoleBindingSet.Of(RoleReference.Auditor),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new InstallAuthorizationDefinition(auditorWrite)).AsTask());

        // Ticket 217: a read definition that does NOT offer the Auditor is admitted -- the Auditor is
        // offered by the platform audit:read definition and by no other, so "read-classified" no longer
        // compels the offer. Dropping it is a narrowing, and narrowing is always allowed.
        var readWithoutAuditor = ReadDefinition() with { OfferedRoles = RoleBindingSet.Of(Author) };
        await writer.WriteAsync(new InstallAuthorizationDefinition(readWithoutAuditor));
    }

    [Fact]
    public async Task DefinitionReplacement_CannotAddOfferedRole()
    {
        var (writer, _, _) = CreateWriter();
        var installed = WriteDefinition();
        await writer.WriteAsync(new InstallAuthorizationDefinition(installed));
        var widened = installed with
        {
            Revision = 2,
            OfferedRoles = RoleBindingSet.Of(Author, Reviewer),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(new ReplaceAuthorizationDefinition(widened)).AsTask());
    }

    [Fact]
    public async Task DefinitionsForRoleAsync_ReturnsDefinitionsThatNameRole()
    {
        var (writer, _, reader) = CreateWriter();
        var definition = WriteDefinition();
        await writer.WriteAsync(new InstallAuthorizationDefinition(definition));

        Assert.Equal(
            [definition],
            await reader.DefinitionsForRoleAsync(Tenant, Author));
    }

    [Fact]
    public async Task UnreferencedRole_HasNoDefinitions()
    {
        var (writer, _, reader) = CreateWriter();
        await writer.WriteAsync(new InstallAuthorizationDefinition(WriteDefinition()));

        Assert.Empty(await reader.DefinitionsForRoleAsync(Tenant, Reviewer));
    }

    [Fact]
    public void TenantBindingIntersection_EqualsPublisherIntersectTenant_ForEverySubsetPair()
    {
        var admission = new AuthorizationCapabilityBindingAdmission();
        var universe = new[] { Author, Reviewer, RoleReference.Administrator };
        var subsets = Subsets(universe);

        foreach (var publisherCeiling in subsets)
        {
            foreach (var tenantSelection in subsets.Where(set => set.IsSubsetOf(publisherCeiling)))
            {
                var admitted = admission.Admit(
                    publisherCeiling,
                    publisherCeiling,
                    tenantSelection);
                Assert.Equal(
                    publisherCeiling.Intersect(tenantSelection),
                    admitted.EffectiveRoles);
            }
        }
    }

    [Fact]
    public void TenantBinding_CannotAddRoleOutsidePublisherCeiling()
    {
        var admission = new AuthorizationCapabilityBindingAdmission();

        Assert.Throws<InvalidOperationException>(() => admission.Admit(
            RoleBindingSet.Of(Author),
            RoleBindingSet.Of(Author),
            RoleBindingSet.Of(Author, Reviewer)));
    }

    [Fact]
    public void TenantBinding_ChangeSequenceIsMonotoneNarrowing()
    {
        var admission = new AuthorizationCapabilityBindingAdmission();
        var subsets = Subsets([Author, Reviewer, RoleReference.Administrator]);

        foreach (var ceiling in subsets)
        {
            foreach (var current in subsets.Where(set => set.IsSubsetOf(ceiling)))
            {
                foreach (var selected in subsets)
                {
                    if (selected.IsSubsetOf(current))
                    {
                        Assert.Equal(
                            selected,
                            admission.Admit(ceiling, current, selected).EffectiveRoles);
                    }
                    else
                    {
                        Assert.Throws<InvalidOperationException>(() =>
                            admission.Admit(ceiling, current, selected));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task EmptyTenantBinding_IsAcceptedWithEmptyBindingWarning()
    {
        var (writer, _, _) = CreateWriter();
        var definition = WriteDefinition();
        await writer.WriteAsync(new InstallAuthorizationDefinition(definition));

        var result = await writer.WriteAsync(new NarrowCapabilityRoleBinding(
            Tenant,
            definition.DefinitionId,
            RoleBindingSet.Empty,
            Actor,
            DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
            new BindingChangeReason("tenant-policy")));

        Assert.Equal(RoleBindingSet.Empty, result.BindingChange!.EffectiveRoles);
        Assert.Equal(BindingWarningCode.EmptyBinding, result.BindingChange.Warning);
    }

    [Fact]
    public void AuthorizationConfigurationStore_CannotReceiveAnUnvalidatedWrite()
    {
        Assert.Empty(typeof(ValidatedAuthorizationConfigurationWrite).GetConstructors());
        var commit = Assert.Single(
            typeof(IAuthorizationConfigurationStore).GetMethods(),
            method => method.Name == "CommitAsync");
        Assert.Equal("CommitAsync", commit.Name);
        Assert.Equal(
            typeof(ValidatedAuthorizationConfigurationWrite),
            commit.GetParameters()[0].ParameterType);

        var repositoryRoot = RepositoryRoot();
        var productionUses = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "packages", "blocks-access-grant"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "new ValidatedAuthorizationConfigurationWrite(",
                StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Equal(["AuthorizationDefinitionWriter.cs"], productionUses);
    }

    private static (AuthorizationDefinitionWriter Writer, InMemoryAuthorizationConfigurationStore Store, IAuthorizationDefinitionReader Reader) CreateWriter()
    {
        var vocabulary = new InMemoryRoleVocabulary(
        [
            RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), "author", "Author", "package-a"),
            RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), "reviewer", "Reviewer", "package-a"),
        ]);
        var store = TestInMemoryAuthorizationStores.ConfigurationStore();
        var writer = new AuthorizationDefinitionWriter(
            store,
            store,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            TestInMemoryAuthorizationStores.GrantStore());
        return (writer, store, store);
    }

    private static AuthorizationCapabilityDefinition ReadDefinition() => new(
        new AuthorizationCapabilityDefinitionId(new Guid("99530e0f-3fdf-45a3-80bb-c92830186301")),
        "package-a",
        1,
        AuthorizationOperation.Parse("records:read"),
        PermissionAtom.Parse("records:read@/"),
        RoleBindingSet.Of(RoleReference.Administrator));

    private static AuthorizationCapabilityDefinition WriteDefinition() => new(
        new AuthorizationCapabilityDefinitionId(new Guid("99530e0f-3fdf-45a3-80bb-c92830186302")),
        "package-a",
        1,
        AuthorizationOperation.Parse("records:write"),
        PermissionAtom.Parse("records:write@/"),
        RoleBindingSet.Of(Author));

    private static IReadOnlyList<RoleBindingSet> Subsets(IReadOnlyList<RoleReference> universe) =>
        Enumerable.Range(0, 1 << universe.Count)
            .Select(mask => RoleBindingSet.From(
                universe.Where((_, index) => (mask & (1 << index)) != 0)))
            .ToArray();

    private sealed class StubIssuanceContext : IGrantIssuanceContext
    {
        public WorkflowEffect BuildGrantWriteEffect(AccessGrant grant, WorkflowStepKey stepKey) =>
            throw new NotSupportedException();
    }

    private sealed class StubRevocationContext : IGrantRevocationContext
    {
        public WorkflowEffect BuildRevokeEffect(
            GrantRevocationRequest request,
            IReadOnlyList<RoleReference> affectedRoles,
            WorkflowStepKey stepKey) => throw new NotSupportedException();
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", ".."));
}
