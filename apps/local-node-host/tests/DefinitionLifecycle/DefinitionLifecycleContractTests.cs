global using Harborline.Api.Foundation.Definitions;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harborline.Api.LocalNodeHost.Tests.ArchTests;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionLifecycle;

public sealed class DefinitionLifecycleContractTests
{
    [Fact]
    public void Public_definition_stores_expose_reads_only()
    {
        var lifecycleMethods = typeof(IDefinitionLifecycleStore<>).GetMethods();

        Assert.Equal(
            [
                "GetAsync",
                "GetCurrentPublishedAsync",
                "ListByTenantAsync",
                "ListPublishedAsync",
            ],
            lifecycleMethods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Contains(
            typeof(IFormDefinitionStore).GetInterfaces(),
            type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDefinitionLifecycleStore<>));
        Assert.Contains(
            typeof(IWorkflowDefinitionStore).GetInterfaces(),
            type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDefinitionLifecycleStore<>));

        var mutations = new[] { "RegisterAsync", "PublishAsync", "WithdrawAsync", "RestorePackProjectionAsync", "DeprecateAsync", "ReplaceAsync" };
        var publicInterfaces = typeof(IFormDefinitionStore).Assembly.GetTypes()
            .Concat(typeof(IWorkflowDefinitionStore).Assembly.GetTypes())
            .Where(IsPublicDefinitionStore);
        Assert.DoesNotContain(
            publicInterfaces.SelectMany(type => type.GetMethods()),
            method => mutations.Contains(method.Name, StringComparer.Ordinal));

        static bool IsPublicDefinitionStore(Type type) =>
            type.IsInterface && type.GetInterfaces().Any(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDefinitionLifecycleStore<>));
    }

    [Fact]
    public void Every_production_type_and_DI_service_keeps_unadmitted_mutation_unreachable()
    {
        var assemblies = new[]
        {
            typeof(IFormDefinitionStore).Assembly,
            typeof(IWorkflowDefinitionStore).Assembly,
        };
        var allTypes = assemblies.SelectMany(static assembly => assembly.GetTypes()).ToArray();
        Assert.DoesNotContain(allTypes, type => type.Name is
            "IFormDefinitionMutationStore" or "IWorkflowDefinitionMutationStore");

        var services = new ServiceCollection();
        Harborline.Api.Foundation.Forms.DependencyInjection.FormsServiceCollectionExtensions
            .AddEntityStoreFormDefinitionStore(services, static _ => throw new InvalidOperationException("descriptor-only test"));
        DurableWorkflowServiceCollectionExtensions.AddEntityStoreWorkflowDefinitionStore(
            services,
            static _ => throw new InvalidOperationException("descriptor-only test"));
        var inMemoryServices = new ServiceCollection();
        Harborline.Api.Foundation.Forms.DependencyInjection.FormsServiceCollectionExtensions
            .AddInMemoryFormDefinitionStore(inMemoryServices);
        var mutationNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "RegisterAsync", "PublishAsync", "WithdrawAsync", "RestorePackProjectionAsync",
            "DeprecateAsync", "ReplaceAsync", "PublishPackAsync", "WithdrawPackAsync", "RestorePackAsync",
        };
        foreach (var serviceType in services.Select(descriptor => descriptor.ServiceType).Distinct())
        {
            foreach (var method in serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(method => (method.IsPublic || method.IsAssembly) && mutationNames.Contains(method.Name)))
            {
                Assert.Contains(method.GetParameters(), parameter =>
                    parameter.ParameterType == typeof(Harborline.Api.Foundation.Authorization.AuthorizationWriteContext)
                    || parameter.ParameterType == typeof(Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority)
                    || parameter.ParameterType.DeclaringType == serviceType
                        && parameter.ParameterType.Name == "WriteAuthority");
            }
        }


        var exposedHandles = ExposedMutationHandles(allTypes);
        Assert.True(exposedHandles.Length == 0, string.Join(Environment.NewLine, exposedHandles));
        Assert.Equal(
            [
                typeof(PlantedBackingHandleOffender).FullName + ".PersistenceStore",
                typeof(PlantedConstructorHandleOffender).FullName + ".ctor(rows)",
                typeof(PlantedReturnedGraphOffender).FullName + ".Snapshot",
            ],
            ExposedMutationHandles(
                [typeof(PlantedBackingHandleOffender), typeof(PlantedConstructorHandleOffender),
                    typeof(PlantedReturnedGraphOffender)]));
    }

    [Fact]
    public void In_memory_definition_state_is_private_and_only_an_opaque_handle_crosses_composition()
    {
        var lifecycle = typeof(AuthorizedFormDefinitionLifecycle);
        var state = lifecycle.GetNestedType("InMemoryFormDefinitionState", BindingFlags.NonPublic);
        Assert.NotNull(state);
        Assert.True(state!.IsNestedPrivate);
        Assert.All(state.GetFields(BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic),
            field => Assert.True(field.IsPrivate));
        Assert.DoesNotContain(
            state.GetMethods(BindingFlags.Instance | BindingFlags.Static
                             | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic),
            method => (method.IsPublic || method.IsAssembly || method.IsFamilyOrAssembly)
                && (method.ReturnType == typeof(SemaphoreSlim)
                    || method.ReturnType == typeof(TimeProvider)
                    || method.ReturnType.IsGenericType
                    && method.ReturnType.GetGenericTypeDefinition() == typeof(Dictionary<,>)));
        Assert.Null(typeof(InMemoryFormDefinitionStore).Assembly.GetType(
            "Harborline.Api.Foundation.Forms.InMemoryFormDefinitionState"));

        var handle = lifecycle.GetNestedType("InMemoryPersistenceHandle", BindingFlags.NonPublic);
        Assert.NotNull(handle);
        Assert.True(handle!.IsSealed);
        Assert.All(handle.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic), constructor =>
            Assert.True(constructor.IsPrivate));
        Assert.Empty(handle.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly
                                      | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Empty(handle.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly
                                          | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Empty(handle.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly
                                       | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_form_severs_author_and_reader_collection_aliases(bool entityStore)
    {
        var roles = new List<string> { "sys.platform-roles/administrator" };
        var tenant = new Harborline.Foundation.Assets.Common.TenantId("frozen-form-tenant");
        IFormDefinitionStore store = entityStore
            ? new EntityStoreFormDefinitionStore(
                new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System), Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, TimeProvider.System)
            : new InMemoryFormDefinitionStore(TimeProvider.System);
        using var disposable = store as IDisposable;
        var lifecycle = Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.FormLifecycle(
            store,
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate());
        var definition = new FormDefinition(
            new FormDefinitionId("frozen-form"),
            new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft,
            tenant,
            IdentityRef.System,
            new SchemaId("schema"),
            new HarborlineOverlay(
                new Dictionary<string, FieldOverlay>(),
                [new FormSection(
                    "main",
                    InternationalizedText.FromInvariant("Main"),
                    [],
                    new SectionAccess(roles, roles))],
                []),
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var registered = await lifecycle.RegisterAsync(
            definition,
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Write(tenant));
        roles.Add("tax.roles/author-retained-mutation");

        Assert.Equal(["sys.platform-roles/administrator"],
            registered.Overlay.Sections.Single().Access.ReadRoles);
        var stored = await store.GetAsync(new DefinitionCoordinates(tenant, "frozen-form", "1.0.0"));
        var storedRoles = stored.Overlay.Sections.Single().Access.ReadRoles;
        Assert.Equal(["sys.platform-roles/administrator"], storedRoles);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)storedRoles).Add("reader-mutation"));
        Assert.Equal(["sys.platform-roles/administrator"],
            (await store.GetAsync(new DefinitionCoordinates(tenant, "frozen-form", "1.0.0")))
            .Overlay.Sections.Single().Access.ReadRoles);
    }

    [Theory]
    [InlineData(typeof(AuthorizedFormDefinitionLifecycle))]
    [InlineData(typeof(AuthorizedWorkflowDefinitionLifecycle))]
    public void Internal_writer_requires_lifecycle_private_key_and_friend_cannot_resolve_or_construct(Type lifecycleType)
    {
        var writer = lifecycleType.GetNestedType("DefinitionWriter", BindingFlags.NonPublic)!;
        var key = lifecycleType.GetNestedType("WriterKey", BindingFlags.NonPublic)!;
        Assert.True(writer.IsNestedAssembly);
        Assert.True(key.IsNestedPrivate);
        var constructor = Assert.Single(writer.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        Assert.Equal(key, constructor.GetParameters()[0].ParameterType);

        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Null(provider.GetService(writer));
        Assert.Throws<MissingMethodException>(() => Activator.CreateInstance(writer));
    }

    [Fact]
    public void Platform_bootstrap_mint_has_only_the_seed_founding_call_site()
    {
        var platformDecision = typeof(AuthorizationDecision).Assembly.GetType(
            "Harborline.Api.Foundation.Authorization.PlatformBootstrapDecision", throwOnError: true)!;
        var mint = Assert.Single(platformDecision.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "Mint");
        var productionAssemblies = new[]
        {
            typeof(AccessGrantAuthorizationSeed).Assembly,
            typeof(AuthorizedFormDefinitionLifecycle).Assembly,
            typeof(AuthorizedWorkflowDefinitionLifecycle).Assembly,
            typeof(ThreeWayMatchDevSeeder).Assembly,
        }.Distinct();
        var callers = productionAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(DeclaredMethods)
            .Where(method => AuthorizationGateArchTests.CalledMethods(method)
                .Any(called => SameMethod(mint, called)))
            .Select(method => method.DeclaringType!.FullName + "." + method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [typeof(AccessGrantAuthorizationSeed).FullName + ".MintPlatformDefinitionBootstrap"],
            callers);
        Assert.Equal(
            [typeof(PlantedPlatformMintOffender).FullName + ".Mint"],
            DeclaredMethods(typeof(PlantedPlatformMintOffender))
                .Where(method => AuthorizationGateArchTests.CalledMethods(method)
                    .Any(called => SameMethod(mint, called)))
                .Select(method => method.DeclaringType!.FullName + "." + method.Name));

        var seedMint = typeof(AccessGrantAuthorizationSeed).GetMethod(
            "MintPlatformDefinitionBootstrap", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(seedMint.IsPrivate);
        Assert.Equal(typeof(AuthorizationDecision), seedMint.GetParameters()[0].ParameterType);
        Assert.Single(seedMint.GetParameters());
        Assert.DoesNotContain(seedMint.GetParameters(), parameter =>
            parameter.ParameterType == typeof(AuthorizationWriteContext));

        var seedMintCallers = productionAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(DeclaredMethods)
            .Where(method => AuthorizationGateArchTests.CalledMethods(method)
                .Any(called => SameMethod(seedMint, called)))
            .ToArray();
        var foundingCaller = Assert.Single(seedMintCallers);
        var install = typeof(AccessGrantAuthorizationSeed).GetMethod(
            "InstallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stateMachine = install.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        Assert.Equal(stateMachine, foundingCaller.DeclaringType);
        Assert.Equal("MoveNext", foundingCaller.Name);

        var tenant = new Harborline.Foundation.Assets.Common.TenantId("mint-evidence");
        var authority = new AuthorizationWriteContext(
            new ActorId("installer:authorization-definition-seed"),
            tenant,
            DateTimeOffset.UnixEpoch);
        var target = Guid.Parse("21800000-0000-0000-0000-000000000218").ToString();
        var request = authority.Request(
            AuthorizationOperation.Parse(Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.GrantPermissions),
            "grant",
            target);
        var wrongEvidence = AuthorizationDecision.CreateBootstrap(request, "some-other-bootstrap");
        var refusal = Assert.Throws<TargetInvocationException>(() => seedMint.Invoke(null, [wrongEvidence]));
        Assert.IsType<ArgumentException>(refusal.InnerException);
        var founding = AuthorizationDecision.CreateBootstrap(request, "administrator-grant-history-absent");
        Assert.IsType<PlatformBootstrapDecision>(seedMint.Invoke(null, [founding]));

        Assert.DoesNotContain(
            productionAssemblies.SelectMany(assembly => assembly.GetTypes())
                .Where(type => type != typeof(AccessGrantAuthorizationSeed))
                .SelectMany(DeclaredMethods)
                .OfType<MethodInfo>()
                .Where(method => method.IsPublic || method.IsAssembly),
            method => method.ReturnType == platformDecision);
    }

    [Fact]
    public void Workflow_registration_accepts_one_authored_wire_and_no_caller_model()
    {
        var registrationMethods = typeof(AuthorizedWorkflowDefinitionLifecycle)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == typeof(AuthorizedWorkflowDefinitionLifecycle))
            .Where(method => method.Name is "RegisterAsync" or "RegisterAndPublishAsync")
            .Where(method => method.IsPublic || method.IsAssembly)
            .ToArray();

        Assert.NotEmpty(registrationMethods);
        Assert.All(registrationMethods, method =>
        {
            Assert.Single(method.GetParameters(), parameter => parameter.ParameterType == typeof(System.Text.Json.JsonElement));
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(WorkflowDefinition));
        });
    }

    [Theory]
    [InlineData("PublishAsync")]
    [InlineData("WithdrawAsync")]
    [InlineData("RestorePackProjectionAsync")]
    public void Pack_workflow_transitions_admit_the_persisted_record(string methodName)
    {
        var lifecycle = typeof(AuthorizedWorkflowDefinitionLifecycle);
        var transition = Assert.Single(lifecycle.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name == methodName
                && method.GetParameters().Length >= 2
                && method.GetParameters()[0].ParameterType == typeof(System.Text.Json.JsonElement)
                && method.GetParameters()[1].ParameterType
                    == typeof(Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority));
        var stateMachine = transition.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = stateMachine.GetMethod(
            "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persistedAdmission = lifecycle.GetMethod(
            "AdmitPersistedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Contains(
            AuthorizationGateArchTests.CalledMethods(moveNext),
            called => SameMethod(persistedAdmission, called));
    }

    [Fact]
    public void Durable_domain_stores_share_the_entity_store_lifecycle_implementation()
    {
        Assert.Equal(
            typeof(EntityStoreDefinitionLifecycle<>),
            typeof(EntityStoreFormDefinitionStore).BaseType!.GetGenericTypeDefinition());
        Assert.Equal(
            typeof(EntityStoreDefinitionLifecycle<>),
            typeof(EntityStoreWorkflowDefinitionStore).BaseType!.GetGenericTypeDefinition());
    }

    [Fact]
    public void Workflow_execution_reads_use_typed_envelope_coordinates()
    {
        Assert.Equal(
            [typeof(DefinitionAddress), typeof(CancellationToken)],
            typeof(IWorkflowDefinitionExecutionStore)
                .GetMethod("GetAdmittedCurrentPublishedAsync")!
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
        Assert.Equal(
            [typeof(DefinitionCoordinates), typeof(CancellationToken)],
            typeof(IWorkflowDefinitionExecutionStore)
                .GetMethod("GetAdmittedAsync")!
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task Noop_adapter_treats_every_public_read_as_an_empty_store_operation()
    {
        IDefinitionLifecycleStore<FormDefinition> store = new NoopFormDefinitionStore();
        var tenant = new Harborline.Foundation.Assets.Common.TenantId("tenant-empty");
        var address = new DefinitionAddress(tenant, "missing.form");
        var coordinates = new DefinitionCoordinates(tenant, "missing.form", "1.0.0");

        await Assert.ThrowsAsync<FormDefinitionNotFoundException>(() => store.GetAsync(coordinates).AsTask());
        Assert.Null(await store.GetCurrentPublishedAsync(address));
        var listed = new List<FormDefinition>();
        await foreach (var definition in store.ListByTenantAsync(tenant))
        {
            listed.Add(definition);
        }
        Assert.Empty(listed);
        await foreach (var definition in store.ListPublishedAsync())
        {
            listed.Add(definition);
        }
        Assert.Empty(listed);
    }

    private static string[] ExposedMutationHandles(IEnumerable<Type> types)
    {
        var offenders = new List<string>();
        foreach (var type in types)
        {
            if (!IsExternallyReachable(type)) continue;
            foreach (var constructor in type.GetConstructors(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!IsExternallyReachable(constructor)) continue;
                foreach (var parameter in constructor.GetParameters())
                {
                    if (IsMutableState(parameter.ParameterType))
                        offenders.Add(type.FullName + ".ctor(" + parameter.Name + ")");
                }
            }
            foreach (var method in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (IsExternallyReachable(method)
                    && ReturnGraphContainsMutableState(method.ReturnType))
                    offenders.Add(type.FullName + "." + method.Name.Replace("get_", string.Empty, StringComparison.Ordinal));
            }
            foreach (var property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var getter = property.GetMethod;
                if (getter is not null
                    && IsExternallyReachable(getter)
                    && ReturnGraphContainsMutableState(property.PropertyType))
                    offenders.Add(type.FullName + "." + property.Name);
            }
            foreach (var field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if ((field.IsPublic || field.IsAssembly || field.IsFamilyOrAssembly)
                    && ReturnGraphContainsMutableState(field.FieldType))
                    offenders.Add(type.FullName + "." + field.Name);
            }
        }
        return offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool ReturnGraphContainsMutableState(Type returnType)
    {
        var valueType = UnwrapReturn(returnType);
        if (IsMutationHandle(valueType)) return true;
        return valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(property => IsMutableState(UnwrapReturn(property.PropertyType)));
    }

    private static Type UnwrapReturn(Type type)
    {
        while (type.IsArray || type.IsGenericType && type.GetGenericArguments().Length == 1
               && type.GetGenericTypeDefinition() is var generic
               && (generic == typeof(Task<>)
                   || generic == typeof(ValueTask<>)
                   || generic == typeof(IAsyncEnumerable<>)
                   || generic == typeof(IEnumerable<>)))
            type = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        return type;
    }

    private static bool IsMutableState(Type type) =>
        type == typeof(SemaphoreSlim)
        || type.Name is "InMemoryFormDefinitionState" or "InMemoryWorkflowDefinitionState"
        || IsMutableDefinitionCollection(type);

    private static bool IsMutableDefinitionCollection(Type type)
    {
        if (type.IsArray) return ContainsStoredDefinition(type.GetElementType()!);
        if (!type.IsGenericType) return false;
        var generic = type.GetGenericTypeDefinition();
        return (generic == typeof(Dictionary<,>)
                || generic == typeof(List<>)
                || type.GetInterfaces().Any(candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() is var contract
                    && (contract == typeof(IDictionary<,>) || contract == typeof(IList<>))))
            && type.GetGenericArguments().Any(ContainsStoredDefinition);
    }

    private static bool ContainsStoredDefinition(Type type) =>
        type == typeof(FormDefinition)
        || type == typeof(WorkflowDefinitionRecord)
        || type.Name is "InMemoryFormDefinitionState" or "InMemoryWorkflowDefinitionState"
        || type.IsArray && ContainsStoredDefinition(type.GetElementType()!)
        || type.IsGenericType && type.GetGenericArguments().Any(ContainsStoredDefinition);

    private static bool IsMutationHandle(Type type) =>
        typeof(IEntityStore).IsAssignableFrom(type)
        || IsMutableState(type)
        || type.Name.Contains("DefinitionWriter", StringComparison.Ordinal);

    private static bool IsExternallyReachable(Type type) =>
        type.IsPublic || type.IsNestedPublic || type.IsNestedAssembly || type.IsNestedFamORAssem
        || type.IsNotPublic && !type.IsNested;

    private static bool IsExternallyReachable(MethodBase method) =>
        method.IsPublic || method.IsAssembly || method.IsFamilyOrAssembly;

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    private static bool SameMethod(MethodBase expected, MethodBase actual) =>
        expected.Module == actual.Module && expected.MetadataToken == actual.MetadataToken;

    public sealed class PlantedBackingHandleOffender
    {
        internal IEntityStore PersistenceStore => null!;
    }

    public sealed class PlantedConstructorHandleOffender(Dictionary<string, FormDefinition> rows)
    {
        public int Count => rows.Count;
    }

    public sealed class PlantedReturnedGraph
    {
        public Dictionary<string, FormDefinition> Rows { get; } = [];
    }

    public sealed class PlantedReturnedGraphOffender
    {
        internal PlantedReturnedGraph Snapshot => new();
    }

    private sealed class PlantedPlatformMintOffender
    {
        internal static PlatformBootstrapDecision Mint(AuthorizationDecision decision) =>
            PlatformBootstrapDecision.Mint(decision);
    }
}
