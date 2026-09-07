using System.Runtime.CompilerServices;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class AuthorizationGateArchTests
{
    private static readonly (string Path, string Reason)[] JustifiedConsumers =
    [
        // Ticket 205 slice 5: NodeDraftPartyComposition.cs (the party-derivation adapter, which now refuses
        // rather than passing a permission string to the ambient context), WebPlaneFencedAuthorizationContext.cs
        // (deleted with its last consumer) and DefaultPaymentApplicationService.cs (the soft-close override
        // resolves through AuthorizationGate.DecideAsync) hold no fenced ambient symbol any more, so their
        // justified-consumer rows went vacuous and were removed.
        // Ticket 205 slice 2: NodeWorkshopUnlockAuthority.cs resolves workshop:unlock through the gate now, so it
        // holds none of the fenced ambient symbols and its justified-consumer row became vacuous.
        ("apps/local-node-host/Data/Financial/ActiveTeamAuthorizationContext.cs",
            "ticket-290 desktop plane: carries the closure reader into EffectiveMemberPermissions, the one reading"),
        ("apps/local-node-host/Data/Identity/AccountSetupAcceptanceService.cs", "account-setup route guard"),
        ("apps/local-node-host/Data/Identity/AdminTeamAccessAuthority.cs", "admin-team route guard"),
        ("apps/local-node-host/Data/Identity/EffectiveMemberPermissions.cs",
            "ticket-211 shared roster-edge-then-closure read, extracted from the two consumers below"),
        ("apps/local-node-host/Data/Identity/SelectedSessionPermissionResolver.cs", "ticket-205 selected-session read consumer"),
        ("apps/local-node-host/Data/Identity/WebAdmittedMemberAtlasBridge.cs", "member-admission route guard"),
        ("apps/local-node-host/Data/Search/ClosureAuthorizedRecordSetProjection.cs", "ticket-205 record-set read consumer"),
        // Ticket 205 slice 4: the nine record-scoped route files (authorization-admin, bank-account,
        // contact, entity, form-definition, invoice, journal-entry, scheduling, spatial-frame) and the
        // shared RequestAuthorization guard they routed through no longer hold a fenced ambient symbol —
        // each act resolves through AuthorizationGate.DecideAsync — so their justified-consumer rows went
        // vacuous and were removed.
        // Ticket 205 slice 3: the four pack and feed route files no longer hold a fenced ambient symbol —
        // they resolve through AuthorizationGate.DecideAsync — so their justified-consumer rows went vacuous.
        ("apps/local-node-host/Health/WebSession/SelectedSessionTenantContext.cs", "selected-session route guard"),
        ("apps/local-node-host/TenantForking/TenantForkService.cs", "tenant-fork route guard"),
        ("packages/blocks-access-grant/GrantIssuanceHandler.cs", "granter coverage migrates in ticket 199 slice 3"),
        // Ticket 257: the blocks-financial-ledger/Services/JournalPostingService.cs row was vacuous — that file
        // holds none of the fenced closure-reader / Covers / HasPermission / AuthorizationEngine symbols. Its
        // raw-journal-port admission is carried by Slice3RawMutationCallers below, which the discovery does find.
    ];

    private static readonly (string Path, string Reason)[] ReaderAndPrimitiveDefinitions =
    [
        ("apps/local-node-host/Data/Authorization/NodeEfAuthorizationClosureReader.cs", "durable closure reader definition"),
        ("apps/local-node-host/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs", "durable reader composition"),
        ("apps/local-node-host/Program.cs", "host reader composition"),
        ("packages/blocks-access-grant/DefinitionJoinedAuthorizationReader.cs", "fallback closure reader definition"),
        ("packages/blocks-access-grant/DependencyInjection/AccessGrantServiceCollectionExtensions.cs", "fallback reader composition"),
        ("packages/blocks-access-grant/IAuthorizationClosureReader.cs", "legacy read-only closure interface definition"),
        ("packages/foundation-identity-atlas/Permissions/PermissionAtomSet.cs", "coverage primitive definition"),
    ];

    // Ticket 205 slice 6: the two legacy point-of-use engine rows are gone with their files. The
    // business-object property engine (AuthorizationEngine, BusinessObjectBase and the rest of
    // packages/foundation/BusinessLogic outside Enums/) had no production or test consumer and was
    // deleted rather than migrated, so the allow-list is empty and removed. The AuthorizationEngine
    // alternative stays in the forbidden pattern below as a resurrection guard, with
    // KernelWriteFence_PlantedOffenderIsDetected_AndGateIsResolvable as its red.

    private static readonly (string Path, string Type, string Reason)[] Slice3RawMutationCallers =
    [
        ("apps/local-node-host/Program.cs", "NodeEfJournalStore", "journal persistence composition root"),
        ("apps/local-node-host/Program.cs", "IJournalStore", "journal persistence composition root"),
        ("apps/local-node-host/Data/Financial/NodeInvoiceWriteComposition.cs", "IJournalStore", "journal enlistment composition root"),
        ("apps/local-node-host/Health/HostedJournalEntryApiEndpoint.cs", "NodeEfJournalStore", "journal route composition"),
        ("apps/local-node-host/Health/JournalEntryRoutes.cs", "NodeEfJournalStore", "admitted journal reversal coordinator"),
        ("packages/blocks-financial-ledger/Services/JournalPostingService.cs", "IJournalStore", "admitted journal posting coordinator"),
        ("apps/local-node-host/Data/Workflow/NodeWorkflowComposition.cs", "IWorkflowStore", "workflow persistence composition root"),
        ("apps/local-node-host/Data/Workflow/NodeWorkflowInstantiationService.cs", "IWorkflowStore", "admitted workflow instance coordinator"),
        ("apps/local-node-host/Data/Workflow/NodeThreeWayMatchWorkflowSeed.cs", "IWorkflowStore", "explicit development seed exception"),
        ("packages/blocks-workflow/src/durable/WorkflowTriggerDispatcher.cs", "IWorkflowStore", "admitted workflow transition coordinator"),
        ("apps/local-node-host/Data/Identity/AccountSetupAcceptanceService.cs", "AccountSetupInvitationStore", "admitted account acceptance coordinator"),
        ("apps/local-node-host/Data/Identity/AccountSetupInvitationIssuer.cs", "AccountSetupInvitationStore", "admitted invitation issuer"),
        ("apps/local-node-host/Data/Identity/AccountCredentialRecoveryService.cs", "RecoveryInvitationStore", "specialized cryptographic recovery ceremony"),
        ("apps/local-node-host/Data/Identity/RecoveryInvitationIssuer.cs", "RecoveryInvitationStore", "admitted recovery invitation issuer"),
        ("apps/local-node-host/KgCalendarDevIndexer.cs", "NodeSearchIndexer", "disabled background projection entry"),
        ("apps/local-node-host/Data/Search/Vector/NodeVecSearchComposition.cs", "NodeVecIndexer", "vector erasure composition root"),
        ("apps/local-node-host/Data/Search/Vector/KgVecIndexSubjectErasurePropagator.cs", "NodeVecIndexer", "specialized crypto-erasure coordinator"),
    ];

    [Fact]
    public void KernelWritePaths_CannotReadClosureOrComputeVerdictOutsideGate()
    {
        var offenders = Scan(RepositoryRoot());
        Assert.True(offenders.Length == 0, "Authorization gate bypasses:\n" + string.Join("\n", offenders));
        Assert.All(JustifiedConsumers.Concat(ReaderAndPrimitiveDefinitions),
            item =>
            {
                Assert.DoesNotContain('*', item.Path);
                Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            });
    }

    [Fact]
    public void KernelWriteFence_PlantedOffenderIsDetected_AndGateIsResolvable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ticket-199-gate-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages", "planted"));
        try
        {
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderWriter.cs"),
                "sealed class OffenderWriter { object Write(dynamic reader, dynamic atom, dynamic act) => reader.ReadAsync().Result ?? atom.Covers(act); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderHandler.cs"),
                "sealed class OffenderHandler { IAuthorizationClosureSnapshotReader reader; }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderAuthority.cs"),
                "sealed class OffenderAuthority { bool Allow(dynamic atom, dynamic act) => atom.Covers(act); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "ConcreteConsumer.cs"),
                "sealed class ConcreteConsumer { NodeEfAuthorizationClosureReader reader; }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "LegacyEngineConsumer.cs"),
                "sealed class LegacyEngineConsumer { AuthorizationEngine engine = new(); }");
            Assert.Equal(
            [
                "packages/planted/ConcreteConsumer.cs",
                "packages/planted/LegacyEngineConsumer.cs",
                "packages/planted/OffenderAuthority.cs",
                "packages/planted/OffenderHandler.cs",
                "packages/planted/OffenderWriter.cs",
            ], Scan(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<NodeLocalSearchDbContext>, UnusedSearchContextFactory>();
        services.AddNodeAuthorizationModel();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.NotNull(provider.GetRequiredService<AuthorizationGate>());
        Assert.NotNull(provider.GetRequiredService<IRecordStandingResolver>());
        Assert.IsType<NodeEfAuthorizationClosureReader>(
            provider.GetRequiredService<IAuthorizationClosureSnapshotReader>());
    }

    [Fact]
    public void ProductionCannotConstructGateOrImplementSnapshotReaderOutsideRealReaders()
    {
        Assert.Empty(ScanGateImplementations(RepositoryRoot()));

        var root = Path.Combine(Path.GetTempPath(), "ticket-199-gate-construction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages", "planted"));
        try
        {
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderLifecycle.cs"),
                "sealed class OffenderLifecycle { object gate = new AuthorizationGate(null!, null!, null!); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderEngine.cs"),
                "sealed class OffenderEngine : IAuthorizationClosureSnapshotReader { }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "OffenderInstaller.cs"),
                "sealed class OffenderInstaller { object gate = new AuthorizationGate(null!, null!, null!); }");
            Assert.Equal(
            [
                "packages/planted/OffenderEngine.cs",
                "packages/planted/OffenderInstaller.cs",
                "packages/planted/OffenderLifecycle.cs",
            ], ScanGateImplementations(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ContextFreeCoordinatorContinuation_HasOnlyFounderAttachAndRecoveryCallers()
    {
        string[] allowed =
        [
            typeof(FounderTenantMembershipAttachService).FullName!,
            typeof(InstallationIdentityCoordinatorRecoveryService).FullName!,
        ];
        var callers = ContinuationCallers(typeof(InstallationIdentityCoordinatorService).Assembly.GetTypes());
        Assert.Equal(allowed.Order(StringComparer.Ordinal), callers);

        Assert.Equal(
            [typeof(CompiledCoordinatorContinuationOffender).FullName!],
            ContinuationCallers([typeof(CompiledCoordinatorContinuationOffender)]));
    }

    [Fact]
    public void OfflineAdministratorRecovery_UngatedAuthorityIsPrivateAndOnlyFactoryCallsIt()
    {
        var factory = typeof(NodeAdministratorAuthority).GetNestedType(
            "NodeAdministratorOfflineRecoveryFactory",
            BindingFlags.NonPublic);
        Assert.NotNull(factory);
        var ungatedConstructor = Assert.Single(
            typeof(NodeAdministratorAuthority).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Length == 2);
        Assert.True(ungatedConstructor.IsPrivate);
        var authority = factory!.GetNestedType("RecoveryAuthority", BindingFlags.NonPublic);
        Assert.NotNull(authority);
        var constructor = Assert.Single(authority!.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        var recovery = Assert.Single(
            authority.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
            method => method.Name == "EstablishByRecoveryAsync");
        Assert.True(recovery.IsPrivate);

        var targets = new MethodBase[] { ungatedConstructor, constructor, recovery };
        var callers = typeof(NodeAdministratorAuthority).Assembly.GetTypes()
            .SelectMany(DeclaredMethods)
            .Where(method => CalledMethods(method).Any(called =>
                targets.Any(target => SameMethod(target, called))))
            .Select(method => method.DeclaringType!)
            .Select(type =>
            {
                while (type.DeclaringType is not null &&
                       type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                    type = type.DeclaringType;
                return type;
            })
            .Distinct()
            .ToArray();

        Assert.NotEmpty(callers);
        Assert.All(callers, caller => Assert.True(
            caller == factory || IsNestedIn(caller, factory),
            $"{caller.FullName} calls private offline recovery outside its factory."));

        static bool IsNestedIn(Type type, Type parent)
        {
            for (var current = type.DeclaringType; current is not null; current = current.DeclaringType)
                if (current == parent) return true;
            return false;
        }
    }

    [Fact]
    public void InvitationBootstrapCapability_HasExactlyTwoFixedDecisionsAndOnlyAcceptanceConstructsIt()
    {
        var capability = typeof(AccessGrantAuthorizationSeed).Assembly.GetType(
            "Harborline.Api.Blocks.AccessGrant.InvitationBootstrapAuthorization",
            throwOnError: true)!;
        Assert.False(capability.IsPublic);
        var constructors = capability.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.All(constructors, item => Assert.False(item.IsPublic));
        var constructor = Assert.Single(constructors);
        Assert.False(constructor.IsPublic);
        Assert.Equal(
            [typeof(string), typeof(TenantId), typeof(DateTimeOffset), typeof(string)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var exposedMethods = capability
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == capability && (method.IsPublic || method.IsAssembly))
            .ToArray();
        Assert.Equal(
            ["AuthorizeInitialGrantIssuance", "AuthorizeMembershipAdmission"],
            exposedMethods
                .Where(method => method.ReturnType == typeof(AuthorizationDecision))
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(constructors.Cast<MethodBase>().Concat(exposedMethods), member =>
            Assert.DoesNotContain(member.GetParameters(), parameter =>
                parameter.ParameterType == typeof(AuthorizationOperation) ||
                parameter.ParameterType == typeof(AuthorizationTarget) ||
                parameter.Name is "operation" or "target" or "recordKind" or "recordId"));
        Assert.Null(typeof(AccessGrantAuthorizationSeed).Assembly.GetType(
            "Harborline.Api.Blocks.AccessGrant.InvitationBootstrapMintedAccountEvidence"));

        var callers = typeof(AccountSetupAcceptanceService).Assembly.GetTypes()
            .SelectMany(DeclaredMethods)
            .Where(method => CalledMethods(method).Any(called => SameMethod(constructor, called)))
            .Select(method => method.DeclaringType!)
            .Select(type =>
            {
                while (type.DeclaringType is not null &&
                       type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                    type = type.DeclaringType;
                return type;
            })
            .Distinct()
            .ToArray();
        Assert.Equal([typeof(AccountSetupAcceptanceService)], callers);
    }

    [Fact]
    public void FormerReadPortsExposeNoRawMutationMethods()
    {
        var mutationNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "RegisterAsync", "PublishAsync", "WithdrawAsync", "RestorePackProjectionAsync",
            "DeprecateAsync", "ReplaceAsync",
        };
        var exposedMutations = typeof(IFormDefinitionStore).Assembly.GetExportedTypes()
            .Concat(typeof(IWorkflowDefinitionStore).Assembly.GetExportedTypes())
            .Where(type => type.IsInterface && type.GetInterfaces().Any(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDefinitionLifecycleStore<>)))
            .SelectMany(type => type.GetMethods())
            .Where(method => mutationNames.Contains(method.Name))
            .ToArray();
        Assert.DoesNotContain(exposedMutations, _ => true);

    }

    [Fact]
    public void AllEnumeratedKernelWriteEntries_AreAdmittedOrExplicitSpecializedSecurityExceptions()
    {
        var inventory = new (string Entry, string Admission)[]
        {
            ("NodeEfJournalStore.SaveAtomicAsync", "JournalPostingService"),
            ("NodeEfJournalStore.ReplaceEntryAsync", "JournalEntryRoutes reversal coordinator"),
            ("NodeEfWorkflowStore.CreateInstanceAsync", "NodeWorkflowInstantiationService"),
            ("NodeEfWorkflowStore.AdvanceAsync", "WorkflowTriggerDispatcher"),
            ("NodeEfWorkflowStore.ParkAsync", "WorkflowTriggerDispatcher"),
            ("AccountSetupInvitationStore.IssueAsync", "AccountSetupInvitationIssuer"),
            ("AccountSetupInvitationStore.RecordUsernameConflictAsync", "AccountSetupAcceptanceService"),
            ("AccountSetupInvitationStore.ConsumeAndReadAsync", "AccountSetupAcceptanceService"),
            ("RecoveryInvitationStore.IssueAsync", "RecoveryInvitationIssuer"),
            ("IGrantStore.AppendAsync", "InitialGrantIssuanceService"),
            ("NodeSearchIndexer.IndexNodeAsync", "originating AuthorizationDecision"),
            ("NodeSearchIndexer.IndexEdgesAsync", "originating AuthorizationDecision"),
            ("NodeSearchIndexer.OnResidencyChangedAsync", "originating AuthorizationDecision"),
            ("NodeSearchIndexer.DeleteRecordAsync", "originating AuthorizationDecision"),
            ("NodeVecIndexer.IndexRecordAsync", "originating AuthorizationDecision"),
            ("NodeVecIndexer.IndexArtifactAsync", "originating AuthorizationDecision"),
            ("NodeVecIndexer.DeleteRecordAsync", "originating AuthorizationDecision"),
            ("RecoveryInvitationStore.BeginOrResumeAsync", "specialized cryptographic recovery ceremony"),
            ("RecoveryInvitationStore.MarkCompletedAsync", "specialized cryptographic recovery ceremony"),
            ("NodeVecIndexer.PurgeSubjectAsync", "specialized crypto-erasure security exception"),
        };

        Assert.Equal(inventory.Length, inventory.Select(row => row.Entry).Distinct(StringComparer.Ordinal).Count());
        Assert.All(inventory, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Entry));
            Assert.False(string.IsNullOrWhiteSpace(row.Admission));
        });

        var reflected = ReflectedSlice3MutationSurface();
        string[] reflectedInventory =
        [
            "Harborline.Api.Blocks.FinancialLedger.Services.InMemoryJournalStore.ReplaceEntry",
            "Harborline.Api.Blocks.FinancialLedger.Services.InMemoryJournalStore.SaveAtomicAsync",
            "Harborline.Api.LocalNodeHost.Data.Financial.NodeEfJournalStore.ReplaceEntryAsync",
            "Harborline.Api.LocalNodeHost.Data.Financial.NodeEfJournalStore.SaveAtomicAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore.ConsumeAndReadAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore.ConsumeAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore.IssueAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore.RecordUsernameConflictAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore.RevokeAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.AbortAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.AbortSessionRevocationAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.AbortSessionSelectionAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.FinalizeAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.FinalizeSessionRevocationAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.FinalizeSessionSelectionAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.PrepareAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.PrepareSessionRevocationAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.EncryptedTenantMembershipAuthorityStore.PrepareSessionSelectionAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationStore.BeginOrResumeAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationStore.IssueAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationStore.MarkCompletedAsync",
            "Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationStore.RevokeAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.NodeSearchIndexer.DeleteRecordAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.NodeSearchIndexer.IndexEdgesAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.NodeSearchIndexer.IndexNodeAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.NodeSearchIndexer.OnResidencyChangedAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeVecIndexer.DeleteRecordAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeVecIndexer.IndexArtifactAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeVecIndexer.IndexRecordAsync",
            "Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeVecIndexer.PurgeSubjectAsync",
            "Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore.AdvanceAsync",
            "Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore.CreateInstanceAsync",
            "Harborline.Api.LocalNodeHost.Data.Workflow.NodeEfWorkflowStore.ParkAsync",
        ];
        Assert.True(reflectedInventory.SequenceEqual(reflected, StringComparer.Ordinal),
            "Reflected raw-port mutation surface:\n" + string.Join("\n", reflected));
        Assert.Contains(
            $"{typeof(PlantedWorkflowStore).FullName}.CreateInstanceAsync",
            ReflectedSlice3MutationSurface([typeof(PlantedWorkflowStore)]));
        Assert.Contains(
            $"{typeof(PlantedWorkflowStore).FullName}.EraseAsync",
            ReflectedSlice3MutationSurface([typeof(PlantedWorkflowStore)]));

        var offenders = ScanSlice3RawMutationCalls(RepositoryRoot());
        Assert.True(offenders.Length == 0, "Slice 3 raw mutation callers:\n" + string.Join("\n", offenders));
        Assert.All(Slice3RawMutationCallers, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));

        var root = Path.Combine(Path.GetTempPath(), "ticket-199-slice3-raw-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "packages", "planted"));
        try
        {
            File.WriteAllText(Path.Combine(root, "packages", "planted", "JournalOffender.cs"),
                "sealed class JournalOffender { NodeEfJournalStore store; object Go(dynamic t, dynamic e) => store.SaveAtomicAsync(t, e); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "WorkflowOffender.cs"),
                "sealed class WorkflowOffender { object Go(IServiceProvider s, dynamic x) => s.GetRequiredService<IWorkflowStore>().AdvanceAsync(x); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "IdentityOffender.cs"),
                "sealed class IdentityOffender { RecoveryInvitationStore store; object Go(dynamic x) => store.IssueAsync(x); }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "SearchOffender.cs"),
                "sealed class SearchOffender { object Go(IServiceProvider s, dynamic x) { var indexer = s.GetRequiredService<NodeSearchIndexer>(); return indexer.IndexNodeAsync(x); } }");
            File.WriteAllText(Path.Combine(root, "packages", "planted", "VecOffender.cs"),
                "sealed class VecOffender { NodeVecIndexer indexer; object Go(dynamic x) => indexer.IndexRecordAsync(x); }");
            Assert.Equal(
            [
                "packages/planted/IdentityOffender.cs:RecoveryInvitationStore.IssueAsync",
                "packages/planted/JournalOffender.cs:NodeEfJournalStore.SaveAtomicAsync",
                "packages/planted/SearchOffender.cs:NodeSearchIndexer.IndexNodeAsync",
                "packages/planted/SearchOffender.cs:NodeSearchIndexer.Resolve",
                "packages/planted/VecOffender.cs:NodeVecIndexer.IndexRecordAsync",
                "packages/planted/WorkflowOffender.cs:IWorkflowStore.AdvanceAsync",
                "packages/planted/WorkflowOffender.cs:IWorkflowStore.Resolve",
            ], ScanSlice3RawMutationCalls(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Ticket 257: the read-method allow-list of the slice-3 mutation-surface reflection, hoisted so the
    /// vacuous-row property can check every row against the type's real declared method surface.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> ReflectedReadAllowListByType = new()
    {
        [typeof(InMemoryJournalStore)] =
            new(StringComparer.Ordinal) { "Snapshot", "FindBySourceReferenceAsync" },
        [typeof(NodeEfJournalStore)] =
            new(StringComparer.Ordinal) { "Snapshot", "FindBySourceReferenceAsync" },
        [typeof(NodeEfWorkflowStore)] =
            new(StringComparer.Ordinal) { "LoadAsync", "FindStepResultAsync" },
        [typeof(AccountSetupInvitationStore)] =
            new(StringComparer.Ordinal) { "ListPendingAsync", "ReadPendingAsync" },
        [typeof(EncryptedTenantMembershipAuthorityStore)] =
            new(StringComparer.Ordinal)
            {
                "GetMembershipAsync", "FindMembershipByGrantAsync", "GetIntentStateAsync",
                "IsAdmissionBlockedAsync",
            },
    };

    private static Dictionary<string, HashSet<string>> ReflectedReadAllowList =>
        ReflectedReadAllowListByType.ToDictionary(
            pair => pair.Key.FullName!, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Ticket 257: rows of the read allow-list as "Type.Method".</summary>
    internal static string[] ReflectedReadAllowListRows() =>
        ReflectedReadAllowListByType
            .SelectMany(pair => pair.Value.Select(method => $"{pair.Key.FullName}.{method}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Ticket 257: the declared public instance method surface those rows are drawn from.</summary>
    internal static string[] DiscoveredReflectedReadMethods() =>
        ReflectedReadAllowListByType.Keys
            .SelectMany(type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => $"{type.FullName}.{method.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReflectedSlice3MutationSurface(Type[]? candidates = null)
    {
        Type[] ports =
        [
            typeof(IJournalStore),
            typeof(IWorkflowStore),
            typeof(ITenantMembershipAuthorityStore),
        ];
        var readAllowList = ReflectedReadAllowList;
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies());
        while (pending.TryDequeue(out var assembly))
        {
            if (!assemblies.TryAdd(assembly.FullName!, assembly))
                continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { pending.Enqueue(Assembly.Load(reference)); }
                catch { /* An optional runtime assembly is irrelevant to the production port surface. */ }
            }
        }

        static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        }

        var types = candidates ?? assemblies.Values.SelectMany(Types).ToArray();
        Type[] explicitMutationTypes =
        [
            typeof(AccountSetupInvitationStore), typeof(RecoveryInvitationStore),
            typeof(NodeSearchIndexer), typeof(NodeVecIndexer),
        ];
        return types
            .Where(type => type.IsClass && !type.IsAbstract && !type.Assembly.IsDynamic &&
                (candidates is not null ||
                    (!type.Assembly.GetName().Name!.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) &&
                     !type.Assembly.GetName().Name!.Equals("tests", StringComparison.OrdinalIgnoreCase))) &&
                (ports.Any(port => port.IsAssignableFrom(type)) || explicitMutationTypes.Contains(type)))
            .SelectMany(type => type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Where(method => !readAllowList.TryGetValue(type.FullName!, out var reads) ||
                    !reads.Contains(method.Name))
                .Select(method => $"{type.FullName}.{method.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly IReadOnlyDictionary<string, string> Slice3MutationMethods =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NodeEfJournalStore"] = "SaveAtomicAsync|ReplaceEntryAsync",
            ["IJournalStore"] = "SaveAtomicAsync|ReplaceEntryAsync",
            ["NodeEfWorkflowStore"] = "CreateInstanceAsync|AdvanceAsync|ParkAsync",
            ["IWorkflowStore"] = "CreateInstanceAsync|AdvanceAsync|ParkAsync",
            ["AccountSetupInvitationStore"] = "IssueAsync|RevokeAsync|ConsumeAsync|RecordUsernameConflictAsync|ConsumeAndReadAsync",
            ["RecoveryInvitationStore"] = "IssueAsync|BeginOrResumeAsync|MarkCompletedAsync|RevokeAsync",
            ["NodeSearchIndexer"] = "IndexNodeAsync|IndexEdgesAsync|OnResidencyChangedAsync|DeleteRecordAsync",
            ["NodeVecIndexer"] = "IndexRecordAsync|IndexArtifactAsync|DeleteRecordAsync|PurgeSubjectAsync",
        };

    /// <summary>Ticket 257: every (file, port type) pair the slice-3 discovery finds, allow-list bypassed.</summary>
    /// <remarks>
    /// The sink is caller-owned on purpose: an earlier revision accumulated into a mutable static, which the
    /// gate fact's scratch-root scan poisoned and xUnit's cross-class parallelism corrupted outright.
    /// </remarks>
    internal static string[] DiscoveredSlice3RawMutationKeys()
    {
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        ScanSlice3RawMutationCalls(RepositoryRoot(), discovered);
        return discovered.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] Slice3RawMutationCallerRows() =>
        Slice3RawMutationCallers.Select(item => $"{item.Path}:{item.Type}").ToArray();

    private static string[] ScanSlice3RawMutationCalls(string root, ICollection<string>? discovered = null)
    {
        var types = string.Join('|', Slice3MutationMethods.Keys.Select(Regex.Escape));
        var declaration = new Regex(
            $@"\b(?<type>{types})\??\s+(?<name>_?[A-Za-z][A-Za-z0-9_]*)",
            RegexOptions.Compiled);
        var resolution = new Regex(
            $@"\bGet(?:RequiredService|Service|Services)\s*<\s*(?<type>{types})\s*>\s*\(\s*\)",
            RegexOptions.Compiled);
        var inferredResolution = new Regex(
            $@"\bvar\s+(?<name>_?[A-Za-z][A-Za-z0-9_]*)\s*=\s*[^;]*?Get(?:RequiredService|Service|Services)\s*<\s*(?<type>{types})\s*>\s*\(\s*\)",
            RegexOptions.Compiled);
        var allowed = Slice3RawMutationCallers
            .Select(item => $"{item.Path}:{item.Type}")
            .ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var item in ProductionFiles(root))
        {
            var source = StripComments(File.ReadAllText(item.File));
            foreach (Match resolved in resolution.Matches(source))
            {
                var type = resolved.Groups["type"].Value;
                discovered?.Add($"{item.Relative}:{type}");
                if (!allowed.Contains($"{item.Relative}:{type}"))
                    offenders.Add($"{item.Relative}:{type}.Resolve");
                var tail = source[(resolved.Index + resolved.Length)..];
                var chained = Regex.Match(tail, $@"^\s*\??\s*\.\s*(?<method>{Slice3MutationMethods[type]})\s*\(");
                if (chained.Success && !allowed.Contains($"{item.Relative}:{type}"))
                    offenders.Add($"{item.Relative}:{type}.{chained.Groups["method"].Value}");
            }
            foreach (Match inferred in inferredResolution.Matches(source))
            {
                var type = inferred.Groups["type"].Value;
                discovered?.Add($"{item.Relative}:{type}");
                var receiver = Regex.Escape(inferred.Groups["name"].Value);
                var tail = source[inferred.Index..];
                var call = Regex.Match(tail, $@"\b{receiver}\s*\.\s*(?<method>{Slice3MutationMethods[type]})\s*\(");
                if (call.Success && !allowed.Contains($"{item.Relative}:{type}"))
                    offenders.Add($"{item.Relative}:{type}.{call.Groups["method"].Value}");
            }
            foreach (Match port in declaration.Matches(source))
            {
                var type = port.Groups["type"].Value;
                discovered?.Add($"{item.Relative}:{type}");
                var receiver = Regex.Escape(port.Groups["name"].Value);
                var call = Regex.Match(source, $@"\b{receiver}\s*\.\s*(?<method>{Slice3MutationMethods[type]})\s*\(");
                if (call.Success && !allowed.Contains($"{item.Relative}:{type}"))
                    offenders.Add($"{item.Relative}:{type}.{call.Groups["method"].Value}");
            }
        }

        return offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static readonly HashSet<string> GateImplementationAllowedReaders = new(StringComparer.Ordinal)
    {
        "apps/local-node-host/Data/Authorization/NodeEfAuthorizationClosureReader.cs",
        "packages/blocks-access-grant/DefinitionJoinedAuthorizationReader.cs",
    };

    internal static IReadOnlyCollection<string> GateImplementationAllowedReaderRows => GateImplementationAllowedReaders;

    /// <summary>Ticket 257: every file the gate-implementation discovery finds, allow-list bypassed.</summary>
    internal static string[] DiscoveredGateImplementationFiles() =>
        ScanGateImplementations(RepositoryRoot(), new HashSet<string>(StringComparer.Ordinal));

    private static string[] ScanGateImplementations(string root) => ScanGateImplementations(root, null);

    private static string[] ScanGateImplementations(string root, IReadOnlySet<string>? allowedOverride)
    {
        var allowedReaders = allowedOverride ?? GateImplementationAllowedReaders;
        var forbidden = new Regex(
            // Ticket 257: the base list is matched as a whole, not just its first entry — ": IOther,
            // IAuthorizationClosureSnapshotReader" evaded the old ":\s*I…" anchor. Parens/braces/'=' are excluded
            // so parameter lists and assignments are not mistaken for a base list.
            @"\bnew\s+AuthorizationGate\s*\(|:\s*[^;{()=]*\bIAuthorizationClosureSnapshotReader\b",
            RegexOptions.Compiled);
        return ProductionFiles(root)
            .Where(item => !allowedReaders.Contains(item.Relative))
            .Where(item => forbidden.IsMatch(StripComments(File.ReadAllText(item.File))))
            .Select(item => item.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ContinuationCallers(IEnumerable<Type> types)
    {
        var continuation = typeof(InstallationIdentityCoordinatorContinuation);
        var coordinator = typeof(InstallationIdentityCoordinatorService);
        var tokenGetters = continuation.GetProperties(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(property => property.GetMethod!)
            .ToArray();
        var internalOverloads = coordinator.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == continuation))
            .ToArray();
        var callers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in types.SelectMany(DeclaredMethods))
        {
            foreach (var called in CalledMethods(method))
            {
                if (!tokenGetters.Any(target => SameMethod(target, called)) &&
                    !internalOverloads.Any(target => SameMethod(target, called)))
                    continue;

                var caller = method.DeclaringType!;
                while (caller.DeclaringType is not null &&
                       caller.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                    caller = caller.DeclaringType;
                callers.Add(caller.FullName!);
            }
        }

        return callers.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    private static bool SameMethod(MethodBase expected, MethodBase actual) =>
        expected.Module == actual.Module && expected.MetadataToken == actual.MetadataToken;

    internal static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;

        var position = 0;
        while (position < il.Length)
        {
            OpCode opCode;
            var first = il[position++];
            if (first == 0xfe)
                opCode = MultiByteOpCodes[il[position++]];
            else
                opCode = SingleByteOpCodes[first];

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo info ? info.GetGenericArguments() : null);
                }
                catch (ArgumentException) { }
                if (called is not null)
                    yield return called;
            }

            position += OperandSize(opCode.OperandType, il, position);
        }
    }

    /// <summary>
    /// Whether <paramref name="method"/> can RETURN — whether its IL contains a <c>ret</c>. A body that
    /// only throws answers nothing, which is what lets an implementation of an ambient authorization
    /// contract be a REFUSAL of the ambient shape rather than a service of it (ticket 205 slice 5).
    /// Decoded through the same opcode tables as <see cref="CalledMethods"/>, so an operand byte that
    /// happens to equal <c>ret</c> is never mistaken for the instruction.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    internal static bool CanReturn(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        // No body at all (abstract, extern, or unreadable) is not a refusal — say it can return.
        if (il is null)
            return true;

        var position = 0;
        while (position < il.Length)
        {
            OpCode opCode;
            var first = il[position++];
            if (first == 0xfe)
                opCode = MultiByteOpCodes[il[position++]];
            else
                opCode = SingleByteOpCodes[first];

            if (opCode == OpCodes.Ret)
                return true;

            position += OperandSize(opCode.OperandType, il, position);
        }

        return false;
    }

    internal static IEnumerable<MethodBase> ReachableMethodsWithinType(MethodBase root, Type boundary)
    {
        var pending = new Queue<MethodBase>();
        var seen = new HashSet<(Module Module, int Token)>();

        void Add(MethodBase method)
        {
            if (!BelongsTo(method.DeclaringType, boundary) ||
                !seen.Add((method.Module, method.MetadataToken)))
                return;
            pending.Enqueue(method);
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } asyncState)
            {
                var moveNext = asyncState.StateMachineType.GetMethod(
                    nameof(IAsyncStateMachine.MoveNext),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext is not null)
                    Add(moveNext);
            }
        }

        Add(root);
        while (pending.TryDequeue(out var method))
        {
            yield return method;
            foreach (var called in CalledMethods(method))
                Add(called);
        }

        static bool BelongsTo(Type? type, Type boundaryType)
        {
            while (type is not null)
            {
                if (type == boundaryType)
                    return true;
                type = type.DeclaringType;
            }
            return false;
        }
    }

    private static int OperandSize(OperandType operandType, byte[] il, int position) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, position) * 4),
        _ => throw new InvalidOperationException($"Unknown IL operand type {operandType}."),
    };

    private static readonly IReadOnlyDictionary<byte, OpCode> SingleByteOpCodes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .Where(opCode => opCode.Size == 1)
            .ToDictionary(opCode => unchecked((byte)opCode.Value));

    private static readonly IReadOnlyDictionary<byte, OpCode> MultiByteOpCodes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .Where(opCode => opCode.Size == 2)
            .ToDictionary(opCode => unchecked((byte)opCode.Value));

    private static IEnumerable<(string File, string Relative)> ProductionFiles(string root) =>
        new[] { "packages", "apps" }
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .DefaultIfEmpty(root)
            .SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Relative: Normalize(root, file)))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase));

    private static string[] Scan(string root) => Scan(root, null);

    /// <summary>Ticket 257: the raw discovery, allow-list bypassed, for the vacuous-row property.</summary>
    internal static string[] DiscoveredGateConsumerPaths() =>
        Scan(RepositoryRoot(), new HashSet<string>(StringComparer.Ordinal));

    internal static string[] JustifiedConsumerRows() => JustifiedConsumers.Select(row => row.Path).ToArray();

    internal static string[] ReaderAndPrimitiveDefinitionRows() =>
        ReaderAndPrimitiveDefinitions.Select(row => row.Path).ToArray();

    internal static (string Path, string Reason)[] GateConsumerAllowListRows() =>
        JustifiedConsumers.Concat(ReaderAndPrimitiveDefinitions).ToArray();

    private static string[] Scan(string root, IReadOnlySet<string>? allowedOverride)
    {
        var forbidden = new Regex(
            @"\bIAuthorizationClosure(?:Snapshot)?Reader\b|\bNodeEfAuthorizationClosureReader\b|\bDefinitionJoinedAuthorizationReader\b|\.\s*Covers\s*\(|\.\s*HasPermission\s*\(|\bAuthorizationEngine\b",
            RegexOptions.Compiled);
        var scanRoots = new[] { "packages", "apps" }
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .DefaultIfEmpty(root);
        var allowed = allowedOverride ?? JustifiedConsumers
            .Concat(ReaderAndPrimitiveDefinitions)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);
        return scanRoots.SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .Select(file => new { File = file, Relative = Normalize(root, file) })
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            .Where(item => !item.Relative.StartsWith("packages/foundation-authorization/", StringComparison.Ordinal))
            .Where(item => !allowed.Contains(item.Relative))
            .Where(item => forbidden.IsMatch(StripComments(File.ReadAllText(item.File))))
            .Select(item => item.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Normalize(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative;
    }

    private static string StripComments(string source) => Regex.Replace(
        source,
        @"//.*?$|/\*.*?\*/",
        string.Empty,
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class UnusedSearchContextFactory : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext() => throw new NotSupportedException();
        public Task<NodeLocalSearchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CompiledCoordinatorContinuationOffender
    {
        internal InstallationIdentityCoordinatorContinuation Read() =>
            InstallationIdentityCoordinatorContinuation.FounderAttachment;
    }

    private sealed class PlantedWorkflowStore : IWorkflowStore
    {
        public Task<WorkflowInstanceRecord?> LoadAsync(string instanceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CreateInstanceAsync(WorkflowInstanceRecord instance, DateTimeOffset at, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowStepIdempotencyRecord?> FindStepResultAsync(WorkflowStepKey key, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AdvanceAsync(
            WorkflowStepKey key, WorkflowEffect? effect, string resultJson, string eventType,
            string eventDataJson, string nextStep, WorkflowStatus nextStatus,
            DateTimeOffset at, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ParkAsync(
            string instanceId, string step, string reasonJson, DateTimeOffset at, int iteration = 0,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task EraseAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
