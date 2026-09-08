using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>ADR 0070/0071 structural fences for the definition-led authorization model.</summary>
public sealed class AuthorizationModelArchTests
{
    private static readonly string[] StoredAuthorityTypes =
        ["RoleDefinition", "AuthorizationRoleRow", "AccessGrant", "GrantRow"];

    private static readonly HashSet<string> InlineRoleConstructionOwners = new(StringComparer.Ordinal)
    {
        // Ticket 257: the "…AccessGrant.RoleDefinition" row was vacuous — the inline-minting discovery finds no
        // call site owned by that type (the live owner of that rule is GrantWriters_CannotMintRolesInline).
        "Harborline.Api.Blocks.AccessGrant.InMemoryRoleVocabulary",
        "Harborline.Api.Blocks.AccessGrant.AccessGrantAuthorizationSeed",
        // Ticket 208 slice 3 (L675): the ONE parse site for a pack-shipped role name. It mints through
        // the same RoleDefinition.CreatePackageRole factory the seed uses, so the entry is unsealed and
        // package-owned by construction and a pack cannot name a sealed platform role.
        "Harborline.Api.Blocks.AccessGrant.PackAuthorizationContentAdmission",
    };

    private static readonly HashSet<string> QualifiedRoleCollectionAllowlist = new(StringComparer.Ordinal)
    {
        "Harborline.Api.Foundation.Forms.Engine.FormEngine",
        "Harborline.Api.Foundation.MissionSpace.DefaultMinimumSpecResolver",
        "Harborline.Api.Blocks.AccessGrant.AuthorizationDefinitionAdmission",
        "Harborline.Api.Blocks.AccessGrant.InMemoryAuthorizationConfigurationStore",
        "Harborline.Api.LocalNodeHost.Data.Authorization.NodeEfAuthorizationConfigurationStore",
    };

    [Fact]
    public void RolesAndGrantRecords_StoreNoPermissionPowers()
    {
        Assert.Empty(ScanRolePowerStorage(RepositoryRoot()));
    }

    [Fact]
    public void GrantBindingAndDelegationRecords_CannotStoreStandingValues()
    {
        Assert.Empty(ScanStandingStorage(RepositoryRoot()));
    }

    [Fact]
    public void StandingStorageScanner_ReportsPlantedGrantControl()
    {
        using var offender = ScratchRepository(
            "StandingGrant.cs",
            "namespace Planted; public sealed record StandingGrant(string Standing);");

        Assert.Equal(["StandingGrant.cs:StandingGrant"], ScanStandingStorage(offender.Root));
    }

    [Fact]
    public void RolePowerScanner_ReportsPlantedPermissionSet()
    {
        using var offender = ScratchRepository(
            "Offender.cs",
            "namespace Planted; public sealed record RoleDefinition(PermissionSet Permissions);");

        Assert.Equal(["Offender.cs:RoleDefinition"], ScanRolePowerStorage(offender.Root));
    }

    [Fact]
    public void GrantWriters_CannotMintRolesInline()
    {
        Assert.Empty(ScanInlineRoleMinting(RepositoryRoot()));
    }

    [Fact]
    public void InlineRoleScanner_ReportsPlantedWriter()
    {
        using var offender = ScratchRepository(
            "GrantWriter.cs",
            "namespace Planted; public sealed class GrantWriter { object Mint() => new RoleDefinition(default, default, \"x\", default, false); }");

        Assert.Equal(["GrantWriter.cs"], ScanInlineRoleMinting(offender.Root));
    }

    [Fact]
    public void AuthorizationCode_HasNoGlobalRoleMembershipCheck()
    {
        Assert.Empty(ScanGlobalRoleMembership(RepositoryRoot()));
    }

    [Fact]
    public void GlobalRoleScanner_ReportsPlantedHasRole()
    {
        using var offender = ScratchRepository(
            "AuthorizationSession.cs",
            "namespace Planted; public sealed class AuthorizationSession { bool Allowed(IUserContext user) => user.Has"
            + "Role(\"Manager\"); }");

        Assert.Equal(["AuthorizationSession.cs"], ScanGlobalRoleMembership(offender.Root));
    }

    [Fact]
    public void AuthorizationScanners_IgnoreUntrackedFilesAndCatchTrackedFiles()
    {
        using var repository = ScratchRepository("tracked/Tracked.cs", "namespace Planted; public sealed class Tracked { }");
        var untracked = Path.Combine(repository.Root, ".platform", "Untracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(untracked)!);
        File.WriteAllText(untracked,
            "namespace Planted; public sealed class Untracked { bool Allowed(IUserContext user) => user.HasRole(\"Manager\"); }");

        Assert.Empty(ScanGlobalRoleMembership(repository.Root));

        Track(repository.Root, ".platform/Untracked.cs");
        Assert.Equal([".platform/Untracked.cs"], ScanGlobalRoleMembership(repository.Root));
    }

    [Fact]
    public void AuthorizationConfigurationPublication_HasOneValidatedOwner()
    {
        Assert.Empty(ScanAuthorizationConfigurationWrites(RepositoryRoot()));

        var commits = typeof(NodeEfAuthorizationConfigurationStore).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == "CommitAsync")
            .ToArray();
        var commit = Assert.Single(commits);
        Assert.Equal(typeof(ValidatedAuthorizationConfigurationWrite), commit.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void PublicationScanner_ReportsPlantedRawStoreWrite()
    {
        using var offender = ScratchRepository(
            "AlternateStore.cs",
            """
            namespace Planted;
            public sealed class AlternateStore
            {
                void Write(dynamic db)
                {
                    db.AuthorizationDefinitions.Add(new object());
                    var sql = "INSERT INTO authorization_binding_revisions VALUES (1)";
                }
            }
            """);

        Assert.Equal(["AlternateStore.cs"], ScanAuthorizationConfigurationWrites(offender.Root));
    }

    [Fact]
    public void HistoricalAuthorizationResolver_ReadsNoNowClosureOrClosureTable()
    {
        Assert.Empty(ScanHistoricalClosureReads(RepositoryRoot()));
    }

    [Fact]
    public void HistoricalClosureScanner_ReportsPlantedClosureRead()
    {
        using var offender = ScratchRepository(
            Path.Combine("packages", "blocks-access-grant", "HistoricalAuthorizationResolver.cs"),
            "namespace Planted; public sealed class HistoricalAuthorizationResolver(IAuthorizationClosureReader closure) { const string Table = \"authorization_principal_atom_closure\"; }");

        Assert.Equal(
            [Path.Combine("packages", "blocks-access-grant", "HistoricalAuthorizationResolver.cs")],
            ScanHistoricalClosureReads(offender.Root));
    }

    [Fact]
    public void ShapeRoles_CannotEnterAuthorizationVocabularyGrantsBindingsOrClosure()
    {
        Assert.Empty(ScanShapeRoleAuthorizationReferences(RepositoryRoot()));

        var roleReference = typeof(RoleReference);
        var shapeRole = typeof(ShapeRole);
        var mapping = typeof(ShapeRoleMapping);
        Assert.False(roleReference.IsAssignableFrom(shapeRole));
        Assert.False(roleReference.IsAssignableFrom(mapping));
        Assert.NotEqual(roleReference.BaseType, shapeRole.BaseType);
        Assert.NotEqual(roleReference.BaseType, mapping.BaseType);
        Assert.DoesNotContain(
            roleReference.GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == shapeRole || parameter.ParameterType == mapping));
        Assert.DoesNotContain(
            roleReference.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Concat(shapeRole.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Concat(mapping.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.Name is "op_Implicit" or "op_Explicit"
                && (method.ReturnType == roleReference
                    || method.GetParameters().Any(parameter => parameter.ParameterType == roleReference)));
    }

    [Fact]
    public void ShapeRoleAuthorizationScanner_ReportsPlantedControl()
    {
        using var offender = ScratchRepository(
            Path.Combine("packages", "blocks-access-grant", "ShapeRoleGrant.cs"),
            "namespace Harborline.Api.Blocks.AccessGrant; public sealed record ShapeRoleGrant(ShapeRole Role);");

        Assert.Equal(
            [Path.Combine("packages", "blocks-access-grant", "ShapeRoleGrant.cs")],
            ScanShapeRoleAuthorizationReferences(offender.Root));
    }

    [Fact]
    public void EveryFixedAuthorizationAdmissionGate_IsReachableFromCompositionRoot()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<NodeLocalSearchDbContext>, UnusedSearchContextFactory>();
        services.AddNodeAuthorizationModel();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider.GetRequiredService<AuthorizationDefinitionAdmission>());
        Assert.NotNull(provider.GetRequiredService<AuthorizationCapabilityBindingAdmission>());
        Assert.NotNull(provider.GetRequiredService<AuthorizationDefinitionWriter>());
        Assert.IsType<NodeEfAuthorizationConfigurationStore>(
            provider.GetRequiredService<IAuthorizationConfigurationStore>());
        Assert.NotNull(provider.GetRequiredService<AuthorizationClosureReconciler>());
        Assert.IsType<NodeEfAuthorizationClosureReader>(
            provider.GetRequiredService<IAuthorizationClosureReader>());
        Assert.IsType<NodeEfAuthorizationConfigurationStore>(
            provider.GetRequiredService<IHistoricalAuthorizationConfigurationReader>());
        Assert.IsType<HistoricalAuthorizationResolver>(
            provider.GetRequiredService<IHistoricalAuthorizationResolver>());
    }

    [Fact]
    public async Task PlatformRoleSeed_HasNoAllPermissionsRoleOrWildcard()
    {
        var roles = await new InMemoryRoleVocabulary().ListAsync();
        Assert.Equal(
            [RoleReference.Administrator, RoleReference.Auditor],
            roles.Select(role => role.Role).OrderBy(role => role.Name, StringComparer.Ordinal));
        Assert.All(roles, role => Assert.DoesNotMatch("(?i)all|wildcard|owner|superuser", role.Role.Name));

        var compositionUses = ProductionSources(RepositoryRoot())
            .Where(source => source.Code.Contains("PermissionCompositions", StringComparison.Ordinal))
            .ToArray();
        var compositionLeaks = compositionUses
            .Where(source => !source.RelativePath.EndsWith(
                    Path.Combine("Permissions", "PermissionCompositions.cs"),
                    StringComparison.OrdinalIgnoreCase)
                && (!Regex.IsMatch(source.Code,
                        @"\b(?:MemberRoster|RosterMember|RosterRecord|TeamMembership|grantedPermissions|founderPermissions|selectedSession)\b")
                    || Regex.IsMatch(source.Code,
                        @"\b(?:AccessGrant|AuthorizationCapabilityDefinition|AuthorizationClosure|PermissionAtom(?:Set)?)\b")))
            .Select(source => source.RelativePath)
            .ToArray();
        Assert.Empty(compositionLeaks);
    }

    private static string[] ScanRolePowerStorage(string root)
    {
        const string forbidden =
            @"\b(?:PermissionSet|PermissionAtomSet|PermissionAtom|AuthorizationCapabilityDefinition(?:Id)?)\b"
            + @"|\bbool\s+\w*(?:All|Wildcard)\w*\b";

        return ProductionSources(root)
            .SelectMany(source => StoredAuthorityTypes.Select(type => new
            {
                source.RelativePath,
                Type = type,
                Shape = ExtractTypeShape(source.Code, type),
            }))
            .Where(candidate => candidate.Shape is not null
                && Regex.IsMatch(candidate.Shape, forbidden, RegexOptions.IgnoreCase))
            .Select(candidate => $"{candidate.RelativePath}:{candidate.Type}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ScanStandingStorage(string root)
    {
        const string authorityRecord = @"\b(?:class|record(?:\s+class)?)\s+(\w*(?:Grant|Binding|Delegation)\w*)\b";
        return ProductionSources(root)
            .SelectMany(source => Regex.Matches(source.Code, authorityRecord)
                .Select(match => new
                {
                    source.RelativePath,
                    Type = match.Groups[1].Value,
                    Shape = ExtractTypeShape(source.Code, match.Groups[1].Value),
                }))
            .Where(candidate => candidate.Shape is not null
                && Regex.IsMatch(candidate.Shape, @"\bStanding(?:Reference|Definition|Rule|Value)?\b"))
            .Select(candidate => $"{candidate.RelativePath}:{candidate.Type}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private const string InlineRoleMintingPattern =
        @"\bnew\s+RoleDefinition\s*\(|\bRoleDefinition\s*[.]\s*(?:CreatePackageRole|CreateTenantRole)\s*\("
        + @"|\bpublic\b[^;{}]*(?:Save|Upsert)\w*Role\w*\s*\("
        + @"|\b(?:Save|Upsert)\w*\s*\([^)]*\bRoleDefinition\b"
        + @"|\b(?:record|class)\s+\w*(?:Request|Command|Payload|Input)\w*\b[^;{]*(?:\([^;{]*\bRoleDefinition\b|\{[^}]*\bRoleDefinition\b)";

    private const string QualifiedRoleCollectionPattern = @"\bRoles\s*[.]\s*Contains\s*\(";

    /// <summary>Ticket 257: the owning types the inline-minting discovery actually finds, for the vacuity property.</summary>
    internal static string[] DiscoveredInlineRoleConstructionOwners() => DiscoveredOwners(InlineRoleMintingPattern);

    /// <summary>Ticket 257: the owning types the qualified-role-collection discovery actually finds.</summary>
    internal static string[] DiscoveredQualifiedRoleCollectionOwners() => DiscoveredOwners(QualifiedRoleCollectionPattern);

    internal static IReadOnlyCollection<string> InlineRoleConstructionOwnerRows => InlineRoleConstructionOwners;

    internal static IReadOnlyCollection<string> QualifiedRoleCollectionAllowlistRows => QualifiedRoleCollectionAllowlist;

    private static string[] DiscoveredOwners(string pattern) =>
        ProductionSources(RepositoryRoot())
            .SelectMany(source => Regex.Matches(source.Code, pattern, RegexOptions.Singleline)
                .Select(match => QualifiedTypeAt(source.Code, match.Index)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ScanInlineRoleMinting(string root) =>
        ProductionSources(root)
            .Where(source => Regex.Matches(source.Code, InlineRoleMintingPattern, RegexOptions.Singleline)
                .Any(match => !InlineRoleConstructionOwners.Contains(QualifiedTypeAt(source.Code, match.Index))))
            .Select(source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ScanGlobalRoleMembership(string root) =>
        ProductionSources(root)
            .Where(source =>
            {
                if (Regex.IsMatch(source.Code, @"\b(?:HasRole|IsInRole)\s*\(")) return true;
                return Regex.Matches(source.Code, QualifiedRoleCollectionPattern)
                    .Any(match => !QualifiedRoleCollectionAllowlist.Contains(
                        QualifiedTypeAt(source.Code, match.Index)));
            })
            .Select(source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ScanAuthorizationConfigurationWrites(string root)
    {
        const string dbSetMutation =
            @"\bAuthorization(?:Roles|Definitions|OfferedRoles|BindingRevisions|BindingRoles|CatalogVersions)\s*[.]\s*(?:Add|AddRange|Update|UpdateRange|Remove|RemoveRange)\s*\(";
        const string entityMutation =
            @"\b(?:Add|AddRange|Update|UpdateRange|Remove|RemoveRange)\s*\(\s*new\s+Authorization(?:Role|Definition|OfferedRole|BindingRevision|BindingRole|CatalogVersion)Row\b"
            + @"|\bSet\s*<\s*Authorization(?:Role|Definition|OfferedRole|BindingRevision|BindingRole|CatalogVersion)Row\s*>\s*\(\s*\)\s*[.]\s*(?:Add|AddRange|Update|UpdateRange|Remove|RemoveRange)\s*\(";
        const string sqlMutation =
            @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+[\""\[']?authorization_(?:roles|capability_definitions|capability_offered_roles|binding_revisions|binding_roles|catalog_version)\b";

        const string qualifiedOwner =
            "Harborline.Api.LocalNodeHost.Data.Authorization.NodeEfAuthorizationConfigurationStore";
        return ProductionSources(root)
            .Where(source => Regex.Matches(source.Code, dbSetMutation)
                .Concat(Regex.Matches(source.Code, entityMutation))
                .Concat(Regex.Matches(source.Code, sqlMutation, RegexOptions.IgnoreCase))
                .Any(match => !string.Equals(
                    QualifiedTypeAt(source.Code, match.Index), qualifiedOwner, StringComparison.Ordinal)))
            .Select(source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ScanHistoricalClosureReads(string root)
    {
        var packageRoot = Path.Combine(root, "packages", "blocks-access-grant");
        if (!Directory.Exists(packageRoot)) return [];
        const string forbidden =
            @"\bIAuthorizationClosureReader\b|\bNodeEfAuthorizationClosureReader\b"
            + @"|\bauthorization_(?:principal_atom_closure|closure_state)\b";
        return Directory.EnumerateFiles(packageRoot, "*HistoricalAuthorization*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "obj" or "bin"))
            .Where(path => Regex.IsMatch(CodeOnly(File.ReadAllText(path)), forbidden, RegexOptions.IgnoreCase))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ScanShapeRoleAuthorizationReferences(string root)
    {
        var guardedRoots = new[]
        {
            Path.Combine(root, "packages", "blocks-access-grant"),
            Path.Combine(root, "packages", "foundation-identity-atlas", "Permissions"),
        };
        var closureFiles = new[]
        {
            Path.Combine(root, "apps", "local-node-host", "Data", "Authorization", "AuthorizationRows.cs"),
            Path.Combine(root, "apps", "local-node-host", "Data", "Authorization", "NodeEfAuthorizationClosureReader.cs"),
        };
        const string forbidden = @"\b(?:ShapeRole\w*|\w*ShapeRole)\b";
        var guardedFiles = guardedRoots
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Concat(closureFiles.Where(File.Exists));
        return guardedFiles
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "obj" or "bin"))
            .Where(path => Regex.IsMatch(CodeOnly(File.ReadAllText(path)), forbidden))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ExtractTypeShape(string source, string typeName)
    {
        var declaration = Regex.Match(source,
            $@"\b(?:class|record(?:\s+class)?)\s+{Regex.Escape(typeName)}\b");
        if (!declaration.Success) return null;

        var nextType = Regex.Match(source[(declaration.Index + declaration.Length)..],
            @"\b(?:class|record(?:\s+class)?|struct|interface|enum)\s+\w+\b");
        var end = nextType.Success
            ? declaration.Index + declaration.Length + nextType.Index
            : source.Length;
        return source[declaration.Index..end];
    }

    private static string QualifiedTypeAt(string source, int position)
    {
        var namespaceMatch = Regex.Match(source, @"\bnamespace\s+([\w.]+)\s*[;{]");
        var prefix = namespaceMatch.Success ? namespaceMatch.Groups[1].Value + "." : string.Empty;
        var type = Regex.Matches(source, @"\b(?:class|record(?:\s+class)?|struct)\s+(\w+)\b")
            .Where(match => match.Index <= position)
            .LastOrDefault();
        return type is null ? prefix.TrimEnd('.') : prefix + type.Groups[1].Value;
    }

    private static IEnumerable<SourceFile> ProductionSources(string root)
    {
        return TrackedFiles(root, "*.cs", ":!**/tests/**")
            .Select(file => new
            {
                File = file,
                Relative = Path.GetRelativePath(root, file),
            })
            .Select(item => new SourceFile(item.Relative, CodeOnly(File.ReadAllText(item.File))));
    }

    private static IEnumerable<string> TrackedFiles(string root, params string[] pathspecs)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        start.ArgumentList.Add("--");
        foreach (var pathspec in pathspecs) start.ArgumentList.Add(pathspec);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git ls-files failed: {error}");

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToArray();
    }

    private static string CodeOnly(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "apps"))
                && Directory.Exists(Path.Combine(current.FullName, "packages")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate repository root from '{thisFile}'.");
    }

    private static ScratchRoot ScratchRepository(string relativePath, string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "ticket-204-authorization-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
        InitializeGitRepository(root);
        Track(root, relativePath);
        return new ScratchRoot(root);
    }

    private static void InitializeGitRepository(string root) => RunGit(root, "init", "--quiet");

    private static void Track(string root, string relativePath) => RunGit(root, "add", "--", relativePath);

    private static void RunGit(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed record SourceFile(string RelativePath, string Code);

    private sealed class ScratchRoot(string root) : IDisposable
    {
        public string Root { get; } = root;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class UnusedSearchContextFactory : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext() => throw new NotSupportedException();
        public Task<NodeLocalSearchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
