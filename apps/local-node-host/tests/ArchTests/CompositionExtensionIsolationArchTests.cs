using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

using Harborline.Api.Foundation.Assets;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Engine.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 368: host-reachable composition extensions resolve only with reviewed seams.</summary>
public sealed class CompositionExtensionIsolationArchTests
{
    private const string DiscoveryPattern = "^(?:AddEntityStore.*|AddHarborline(?:.*Engine|EngineRoom)|AddNode.*Composition)$";
    private const string ProductionAssemblyPrefix = "Harborline.";
    private static readonly Regex DiscoveryRegex = new(DiscoveryPattern, RegexOptions.CultureInvariant);
    private sealed record Extension(MethodInfo Method);
    private sealed record CompositionSource(string Text, int FileCount);
    internal sealed record CrossExtensionCollaborator(string Extension, string Type, string Reason);

    // This is the one mechanism the fence applies. A row is live only when withholding it breaks its extension.
    internal static readonly CrossExtensionCollaborator[] KnownCrossExtensionCollaborators =
    [
        new("AddEntityStoreFormDefinitionStore", "IEntityStore", "entity-backed definition storage"),
        new("AddEntityStoreFormDefinitionStore", "IEntityMutationStore", "entity-backed definition mutation"),
        new("AddEntityStoreFormDefinitionStore", "AuthorizationGate/IRoleGateAdmission", "definition lifecycle admission"),
        new("AddEntityStoreWorkflowDefinitionStore", "IEntityStore", "entity-backed workflow storage"),
        new("AddEntityStoreWorkflowDefinitionStore", "IEntityMutationStore", "entity-backed workflow mutation"),
        new("AddEntityStoreWorkflowDefinitionStore", "AuthorizationGate/IRoleGateAdmission", "workflow lifecycle admission"),
        new("AddHarborlineFormEngine", "IEntityMutationStore", "authorized form writer mutation store"),
        new("AddHarborlineFormEngine", "AuthorizationGate/IRoleGateAdmission", "form-engine authorization"),
        new("AddHarborlineFormEngine", "ITenantKeyProvider/IFieldEncryptor", "field encryption substrate"),
        new("AddHarborlineFormEngine", "NodeFormsComposition", "shared forms composition supplies schema, audit, signer, and definition-store substrates"),
    ];

    private readonly ITestOutputHelper output;
    public CompositionExtensionIsolationArchTests(ITestOutputHelper output) => this.output = output;

    [Fact(DisplayName = "Ticket 368: every Program-reachable entity-store or engine extension resolves its own registered graph")]
    public void ProgramReachableExtensions_ResolveEveryRegisteredService()
    {
        var assemblies = ProductionAssemblies();
        var source = HostCompositionSource();
        var extensions = DiscoverExtensions(assemblies, source);
        output.WriteLine($"Discovery regex: {DiscoveryPattern}");
        output.WriteLine($"Production assembly prefix: {ProductionAssemblyPrefix}");
        output.WriteLine($"Discovered production assemblies ({assemblies.Length}): {string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name))}");
        output.WriteLine($"Composition source files (expected/found >= 1/{source.FileCount})");
        output.WriteLine($"Discovered inventory ({extensions.Length}): {string.Join(", ", extensions.Select(e => e.Method.Name))}");
        Assert.NotEmpty(extensions);
        foreach (var extension in extensions) Resolve(extension, except: null);
    }

    [Fact(DisplayName = "Ticket 368: a discovered extension is named when no host composition source invokes it")]
    public void CrdtEngine_IsReachedByHostComposition()
    {
        var assemblies = ProductionAssemblies();
        var source = HostCompositionSource();
        var extension = Assert.Single(ExtensionCandidates(assemblies), candidate => candidate.Method.Name == "AddHarborlineCrdtEngine");
        Assert.True(IsInvoked(source.Text, extension.Method.Name),
            $"Discovered extension is unreached by host composition source: {extension.Method.DeclaringType!.FullName}.{extension.Method.Name}");
    }

    [Fact(DisplayName = "Ticket 368: every allow-list row burns down when its collaborator is no longer needed")]
    public void AllowListRows_AreRequiredByTheirExtension()
    {
        var expected = KnownCrossExtensionCollaboratorRows();
        var found = RequiredCollaboratorRows();
        var stale = expected.Except(found, StringComparer.Ordinal).ToArray();
        var missing = found.Except(expected, StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0 && missing.Length == 0,
            "Allow-list and live required collaborators differ. Stale rows mask nothing; missing rows are unreviewed:\n"
            + $"stale: {string.Join(", ", stale)}\nmissing: {string.Join(", ", missing)}");
    }

    [Fact(DisplayName = "Ticket 368 mutation: removing a needed allow-list row names its extension and collaborator")]
    public void UnapprovedCollaborator_IsNamed()
    {
        var row = Assert.Single(KnownCrossExtensionCollaborators, item => item.Extension == "AddHarborlineFormEngine" && item.Type == "NodeFormsComposition");
        var error = Assert.ThrowsAny<Exception>(() => Resolve(FindExtension(row.Extension), row));
        Assert.Contains(row.Extension, error.Message, StringComparison.Ordinal);
        Assert.Contains("IFormDefinitionStore", error.Message, StringComparison.Ordinal);
    }

    internal static string[] KnownCrossExtensionCollaboratorRows() =>
        [.. KnownCrossExtensionCollaborators.Select(Row).Order(StringComparer.Ordinal)];
    internal static string[] DiscoveredKnownCrossExtensionCollaborators() => RequiredCollaboratorRows();

    private static string[] RequiredCollaboratorRows() =>
        [.. KnownCrossExtensionCollaborators.Where(row => !CanResolve(FindExtension(row.Extension), row)).Select(Row).Order(StringComparer.Ordinal)];
    private static string Row(CrossExtensionCollaborator row) => $"{row.Extension}:{row.Type}";
    private static bool CanResolve(Extension extension, CrossExtensionCollaborator except)
    {
        try { Resolve(extension, except); return true; } catch { return false; }
    }

    private static void Resolve(Extension extension, CrossExtensionCollaborator? except)
    {
        var services = Bootstrap();
        var first = services.Count;
        Invoke(extension.Method, services);
        var registered = services.Skip(first).ToArray();
        foreach (var row in KnownCrossExtensionCollaborators.Where(row => row.Extension == extension.Method.Name && row != except)) Apply(row, services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        foreach (var descriptor in registered.Where(d => !d.ServiceType.IsGenericTypeDefinition))
        {
            try
            {
                var resolver = descriptor.Lifetime == ServiceLifetime.Scoped ? scope.ServiceProvider : provider;
                _ = descriptor.IsKeyedService ? resolver.GetRequiredKeyedService(descriptor.ServiceType, descriptor.ServiceKey) : resolver.GetRequiredService(descriptor.ServiceType);
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"{extension.Method.Name} cannot resolve registered {descriptor.ServiceType.FullName}: {error.Message}");
            }
        }
    }

    private static ServiceCollection Bootstrap()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>("LocalNode:DataDirectory", Path.GetTempPath())]).Build());
        services.AddLogging();
        services.AddTestKernelClock(); // shared composition-test minimum: configuration, logging, TimeProvider only.
        return services;
    }

    private static Extension[] DiscoverExtensions() => DiscoverExtensions(ProductionAssemblies(), HostCompositionSource());

    private static Extension[] DiscoverExtensions(Assembly[] assemblies, CompositionSource source) =>
        ExtensionCandidates(assemblies)
            .Where(extension => IsInvoked(source.Text, extension.Method.Name))
            .ToArray();

    private static Extension[] ExtensionCandidates(IEnumerable<Assembly> assemblies) =>
        assemblies.SelectMany(Types).SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.IsDefined(typeof(ExtensionAttribute), false) && DiscoveryRegex.IsMatch(method.Name)
                && method.GetParameters() is [{ ParameterType: var type }, ..] && type == typeof(IServiceCollection))
            .Where(HasSupportedCompositionSignature)
            .Select(method => new Extension(method)).OrderBy(extension => extension.Method.Name, StringComparer.Ordinal).ToArray();

    private static bool HasSupportedCompositionSignature(MethodInfo method) => method.GetParameters().Skip(1).All(parameter =>
        parameter.HasDefaultValue || parameter.ParameterType == typeof(Func<IServiceProvider, IEntityMutationStore>));

    private static bool IsInvoked(string source, string methodName) =>
        Regex.IsMatch(source, $@"\.{Regex.Escape(methodName)}\s*\(");

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    private static Assembly[] ProductionAssemblies()
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(Program).Assembly]);
        while (pending.TryDequeue(out var assembly))
        {
            var name = assembly.GetName().Name!;
            if (!name.StartsWith(ProductionAssemblyPrefix, StringComparison.Ordinal)
                || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || !assemblies.TryAdd(assembly.FullName!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var referenceName = reference.Name!;
                if (referenceName.StartsWith(ProductionAssemblyPrefix, StringComparison.Ordinal)
                    && !referenceName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return assemblies.Values.OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).ToArray();
    }

    private static CompositionSource HostCompositionSource([CallerFilePath] string file = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)!)!)!;
        var files = Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);
        return new CompositionSource(string.Join(Environment.NewLine, files.Select(path => StripCommentsAndStrings(File.ReadAllText(path)))), files.Length);
    }

    private static string StripCommentsAndStrings(string source)
    {
        var stripped = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index = StripToLineEnd(source, stripped, index + 2) - 1;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index = StripBlockComment(source, stripped, index + 2) - 1;
                continue;
            }

            if (source[index] == '\"' || source[index] == '\'')
            {
                index = StripLiteral(source, stripped, index) - 1;
                continue;
            }

            stripped.Append(source[index]);
        }

        return stripped.ToString();
    }

    private static int StripToLineEnd(string source, StringBuilder stripped, int index)
    {
        for (; index < source.Length && source[index] != '\r' && source[index] != '\n'; index++) stripped.Append(' ');
        return index;
    }

    private static int StripBlockComment(string source, StringBuilder stripped, int index)
    {
        stripped.Append("  ");
        for (; index < source.Length; index++)
        {
            if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
            {
                stripped.Append("  ");
                return index + 2;
            }

            stripped.Append(source[index] is '\r' or '\n' ? source[index] : ' ');
        }

        return index;
    }

    private static int StripLiteral(string source, StringBuilder stripped, int index)
    {
        var quote = source[index];
        var quoteCount = 1;
        while (index + quoteCount < source.Length && source[index + quoteCount] == quote) quoteCount++;
        var verbatim = quote == '\"' && index > 0 && source[index - 1] == '@';
        for (var count = 0; count < quoteCount; count++) stripped.Append(' ');
        index += quoteCount;

        for (; index < source.Length; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                stripped.Append(source[index]);
                continue;
            }

            if (source[index] == quote)
            {
                var closingCount = 1;
                while (index + closingCount < source.Length && source[index + closingCount] == quote) closingCount++;
                if (closingCount >= quoteCount)
                {
                    for (var count = 0; count < quoteCount; count++) stripped.Append(' ');
                    return index + quoteCount;
                }

                if (verbatim && quoteCount == 1)
                {
                    stripped.Append("  ");
                    index++;
                    continue;
                }
            }

            if (!verbatim && quoteCount == 1 && source[index] == '\\' && index + 1 < source.Length)
            {
                stripped.Append("  ");
                index++;
                continue;
            }

            stripped.Append(' ');
        }

        return index;
    }

    private static Extension FindExtension(string name) => Assert.Single(DiscoverExtensions(), extension => extension.Method.Name == name);
    private static void Invoke(MethodInfo method, IServiceCollection services)
    {
        var arguments = method.GetParameters().Select(parameter => parameter.Position switch
        {
            0 => (object)services,
            _ when parameter.ParameterType == typeof(Func<IServiceProvider, IEntityMutationStore>) => (Func<IServiceProvider, IEntityMutationStore>)EntityMutations,
            _ when parameter.HasDefaultValue => parameter.DefaultValue,
            _ => throw new Xunit.Sdk.XunitException($"{method.Name} has an unsupported composition argument {parameter.ParameterType.FullName}"),
        }).ToArray();
        _ = method.Invoke(null, arguments);
    }

    private static void AddEntityStoreReader(IServiceCollection services) =>
        services.AddSingleton<IEntityStore>(_ => new InMemoryEntityStoreReader(new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System)));
    private static void AddEntityMutationStore(IServiceCollection services) =>
        services.AddSingleton<IEntityMutationStore>(_ => new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System));
    private static void AddFieldEncryption(IServiceCollection services)
    {
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
    }
    private static void AddAuthorization(IServiceCollection services) => services.AddTestAuthorizationGate();
    private static void AddNodeForms(IServiceCollection services) => services.AddTestNodeForms();
    private static void Apply(CrossExtensionCollaborator row, IServiceCollection services)
    {
        switch (row.Type)
        {
            case "IEntityStore": AddEntityStoreReader(services); break;
            case "IEntityMutationStore": AddEntityMutationStore(services); break;
            case "AuthorizationGate/IRoleGateAdmission": AddAuthorization(services); break;
            case "ITenantKeyProvider/IFieldEncryptor": AddFieldEncryption(services); break;
            case "NodeFormsComposition": AddNodeForms(services); break;
            default: throw new Xunit.Sdk.XunitException($"Unknown allow-list collaborator {row.Extension}:{row.Type}");
        }
    }
    private static IEntityMutationStore EntityMutations(IServiceProvider provider) => provider.GetRequiredService<IEntityMutationStore>();
}
