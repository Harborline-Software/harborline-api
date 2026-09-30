using System.Reflection;
using System.Text.RegularExpressions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ck-10 (DES-0029): every production write of an authorization definition or tenant binding goes through
/// <see cref="AuthorizationDefinitionWriter"/>'s six stages. The compiled IL of every production Harborline
/// assembly is scanned for each way to reach those records: minting the validated seal, committing it to
/// a configuration store, staging it into the EF store, and constructing, setting or container-writing a
/// governed row. The discovered call sites must equal the reviewed inventory below, so a writer added
/// anywhere else (the host has InternalsVisibleTo on the block, so the type system alone does not stop it)
/// reds this fence. The planted bypass further down proves the scan sees each shape.
/// </summary>
public sealed partial class AuthorizationDefinitionWriteFenceTests
{
    private static readonly Type[] GovernedRows =
    [
        typeof(AuthorizationDefinitionRow),
        typeof(AuthorizationOfferedRoleRow),
        typeof(AuthorizationBindingRevisionRow),
        typeof(AuthorizationBindingRoleRow),
    ];

    private static readonly string[] ContainerWriteVerbs =
        ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "AttachRange", "Update", "UpdateRange",
         "Remove", "RemoveRange", "Entry", "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync"];

    /// <summary>Reviewed inventory: path | caller | sink. Every row names why it is on the pipeline.</summary>
    private static readonly (string Key, string Reason)[] Reviewed =
    [
        // The pipeline itself: validate mints the seal, commit hands it to the store.
        ("packages/blocks-access-grant/AuthorizationDefinitionWriter.cs|Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionWriter+ConfigurationWrite.ValidateAsync|seal",
            "validate stage (run by WritePipeline.RunAsync): the only ordinary mint"),
        ("packages/blocks-access-grant/AuthorizationDefinitionWriter.cs|Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionWriter+ConfigurationWrite.CommitAsync|commit",
            "commit stage (run by WritePipeline.RunAsync)"),
        // The admission conferral runs the same six stages inside the caller's fence: its validate stage mints
        // one seal per derived definition, its commit stage hands them to the caller's unit, and that unit's
        // commit is the only other staging of a sealed write.
        ("packages/blocks-access-grant/AuthorizationDefinitionWriter.cs|Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionWriter.ValidateConferralAsync|seal",
            "admission conferral: validate stage"),
        ("packages/blocks-access-grant/AuthorizationDefinitionWriter.cs|Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionWriter+ConferralWrite.CommitAsync|conferral-commit",
            "admission conferral: commit stage"),
        ("apps/local-node-host/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs|Harborline.Api.LocalNodeHost.Data.Authorization.NodeEfAuthorizationConfigurationStore+ConferralUnit.CommitAsync|stage-write",
            "admission conferral: the commit stage's staging, in the caller's fence"),
        // The EF store's one staging method, reached from its seal-taking commit.
        ("apps/local-node-host/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs|Harborline.Api.LocalNodeHost.Data.Authorization.NodeEfAuthorizationConfigurationStore+<>c__DisplayClass.<CommitCoreAsync>b__0|stage-write",
            "store commit of a pipeline seal"),
    ];

    internal static string[] ReviewedRows() => [.. Reviewed.Select(row => row.Key)];

    /// <summary>Every governed-record write site outside the EF store's own staging method.</summary>
    internal static string[] DiscoveredPipelineWriteKeys() =>
        [.. Discover(ProductionAssemblies()).Where(key => !IsInsideStageWrite(key))];

    [Fact]
    public void EveryProductionWriteOfAGovernedAuthorizationRecordIsOnTheReviewedPipelineInventory()
    {
        var discovered = Discover(ProductionAssemblies());

        // Row construction and row writes belong to the store's StageWriteAsync alone, and that method's
        // callers are themselves inventoried, so its rows need no line of their own here.
        var outsideStage = discovered.Where(key => !IsInsideStageWrite(key)).ToArray();
        Assert.True(ReviewedRows().Order(StringComparer.Ordinal).SequenceEqual(outsideStage.Order(StringComparer.Ordinal)),
            "Authorization-definition write inventory mismatch. Discovered:\n" + string.Join("\n", outsideStage));
        Assert.Contains(discovered, IsInsideStageWrite);
        Assert.All(Reviewed, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
    }

    // ck-10: the admission conferral is a pipeline writer, not an inventoried exception. Its seal is minted
    // only by its validate stage and the caller's unit commits only from its commit stage, so a conferral that
    // stages or commits from anywhere else is a discovered site missing from this list.
    [Fact]
    public void AdmissionConferralIsAPipelineWriterNotAnInventoriedException()
    {
        Assert.All(Reviewed, row => Assert.False(row.Reason.StartsWith("GAP", StringComparison.Ordinal), row.Key));
        var discovered = DiscoveredPipelineWriteKeys();
        const string writer = "packages/blocks-access-grant/AuthorizationDefinitionWriter.cs|Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionWriter";
        Assert.Contains(writer + ".ValidateConferralAsync|seal", discovered);
        Assert.Equal(writer + "+ConferralWrite.CommitAsync|conferral-commit",
            Assert.Single(discovered, key => key.EndsWith("|conferral-commit", StringComparison.Ordinal)));
        Assert.Single(discovered, key => key.Contains("+ConferralUnit.", StringComparison.Ordinal));
    }

    [Fact]
    public void PlantedBypassOutsideTheWriterIsCaughtForEveryWriteShape()
    {
        var planted = Discover([typeof(PlantedAuthorizationWriteBypass).Assembly],
            type => type == typeof(PlantedAuthorizationWriteBypass) || type.DeclaringType == typeof(PlantedAuthorizationWriteBypass));

        foreach (var sink in new[] { "seal", "conferral-commit", "commit", "row-new", "row-set", "row-write" })
            Assert.Contains(planted, key => key.EndsWith("|" + sink, StringComparison.Ordinal));
        Assert.All(planted, key => Assert.DoesNotContain(key, Reviewed.Select(row => row.Key)));
        Assert.DoesNotContain(planted, IsInsideStageWrite);
    }

    [Fact]
    public void NoProductionSqlWritesAGovernedAuthorizationTable()
    {
        var root = RepositoryRoot();
        var offenders = new[] { "apps", "packages" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(path => !Regex.IsMatch(path, @"[\\/](tests|obj|bin|Migrations)[\\/]"))
            .Where(path => SqlWrite().IsMatch(File.ReadAllText(path)))
            .ToArray();
        Assert.Empty(offenders);
        Assert.Matches(SqlWrite(), "INSERT INTO authorization_binding_roles (tenant_id) VALUES ('t')");
    }

    [GeneratedRegex(@"(?i)\b(insert\s+(or\s+\w+\s+)?into|update|delete\s+from)\s+""?authorization_(capability_definitions|capability_offered_roles|binding_revisions|binding_roles)\b")]
    private static partial Regex SqlWrite();

    private static bool IsInsideStageWrite(string key) =>
        key.StartsWith("apps/local-node-host/Data/Authorization/NodeEfAuthorizationConfigurationStore.cs|", StringComparison.Ordinal)
        && key.Contains("StageWriteAsync", StringComparison.Ordinal)
        && (key.EndsWith("|row-new", StringComparison.Ordinal) || key.EndsWith("|row-set", StringComparison.Ordinal)
            || key.EndsWith("|row-write", StringComparison.Ordinal));

    private static string[] Discover(IEnumerable<Assembly> assemblies, Func<Type, bool>? typeFilter = null)
    {
        var sinks = new Dictionary<string, string>(StringComparer.Ordinal);
        return RawMutationPortSymbolInventoryTests.DiscoverCalls(assemblies, target =>
            {
                if (Sink(target) is not { } sink) return false;
                sinks[MethodSignatureSymbol.Format(target)] = sink;
                return true;
            }, typeFilter)
            .Select(site => $"{site.Path}|{Caller(site.Symbol)}|{sinks[site.Target]}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // The closure's scope ordinal differs between Debug and Release (DisplayClass_1 vs _0), and CI gates on
    // Release, so the key drops it. The caller method name inside <...> still identifies the closure.
    private static string Caller(string symbol) =>
        ClosureScopeOrdinal().Replace(symbol[..symbol.IndexOf('(', StringComparison.Ordinal)], "<>c__DisplayClass");

    [GeneratedRegex(@"<>c__DisplayClass_\d+")]
    private static partial Regex ClosureScopeOrdinal();

    internal static string? Sink(MethodBase target)
    {
        var declaring = target.DeclaringType;
        if (declaring is null) return null;
        if (declaring == typeof(ValidatedAuthorizationConfigurationWrite) && target.IsConstructor) return "seal";
        if (target.Name is nameof(IAuthorizationConfigurationStore.CommitAsync)
                or nameof(IAuthorizationConfigurationStore.CommitBootstrapAsync)
            && typeof(IAuthorizationConfigurationStore).IsAssignableFrom(declaring)) return "commit";
        if (declaring == typeof(NodeEfAuthorizationConfigurationStore) && target.Name == "StageWriteAsync") return "stage-write";
        if (target.Name == nameof(IAdmissionConferralUnit.CommitAsync)
            && typeof(IAdmissionConferralUnit).IsAssignableFrom(declaring)) return "conferral-commit";
        if (GovernedRows.Contains(declaring))
            return target.IsConstructor ? "row-new" : target.Name.StartsWith("set_", StringComparison.Ordinal) ? "row-set" : null;
        if (ContainerWriteVerbs.Contains(target.Name) && GenericArguments(target).Any(GovernedRows.Contains)) return "row-write";
        return null;
    }

    private static IEnumerable<Type> GenericArguments(MethodBase target) =>
        (target.IsGenericMethod ? target.GetGenericArguments() : [])
        .Concat(target.DeclaringType!.IsGenericType ? target.DeclaringType.GetGenericArguments() : []);

    /// <summary>Every production Harborline assembly the host test run loads, not a hand-picked list.</summary>
    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(path => Assembly.LoadFrom(path))
            .Where(assembly => assembly.GetReferencedAssemblies().Any(reference =>
                reference.Name == typeof(AuthorizationDefinitionWriter).Assembly.GetName().Name)
                || assembly == typeof(AuthorizationDefinitionWriter).Assembly)
            .ToArray();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "packages")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    /// <summary>
    /// Test-only planted bypass: a "second writer" outside the pipeline that reaches the governed records by
    /// every shape the fence knows. It is never executed.
    /// </summary>
    private sealed class PlantedAuthorizationWriteBypass(
        IAuthorizationConfigurationStore store, IAdmissionConferralUnit unit, NodeLocalSearchDbContext db)
    {
        public async Task Bypass(AuthorizationCapabilityDefinition definition)
        {
            var write = new ValidatedAuthorizationConfigurationWrite(
                AuthorizationConfigurationWriteKind.InstallDefinition, definition, null, 0, 0,
                DateTimeOffset.UnixEpoch, new TenantId("planted"));
            await store.CommitAsync(write);
            await unit.CommitAsync(null!, default);
            db.AuthorizationDefinitions.Add(new AuthorizationDefinitionRow
            {
                DefinitionId = "planted", Revision = 1, PublisherPackageId = "planted", Operation = "planted",
                ScopeType = 0, ScopeValue = "/",
            });
            var existing = await db.AuthorizationBindingRevisions.FirstAsync();
            existing.Revision = 99;
            await db.AuthorizationBindingRoles.ExecuteDeleteAsync();
        }
    }
}
