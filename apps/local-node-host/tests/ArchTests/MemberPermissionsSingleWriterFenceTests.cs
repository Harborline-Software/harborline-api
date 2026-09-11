using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 362 slice 2 — there is exactly ONE writer of a member's conferred permission set, and it is
/// <see cref="IAdminTeamAccessAuthority.NarrowMemberGrantAsync"/> (revoke-and-reissue in one store
/// transaction).
/// <para>
/// Ticket 204 retired permission-bundle mutation and left <c>UpdateMemberPermissionsAsync</c> behind as a
/// fail-closed compatibility shim: an authority method, an interface member, a mounted HTTP route and a
/// result type that could only ever answer <c>NotFound</c>. A shim is a second writer the moment somebody
/// fills it in — the route, the gate call and the self-lockout guard were all still there — so slice 2
/// deleted it rather than leaving it registered. This fence is what stops it coming back: reflection over
/// the production assembly for the symbol, and a source scan for the spelling anywhere in production
/// source (which catches a copy in an assembly this test never loads).
/// </para>
/// </summary>
public sealed class MemberPermissionsSingleWriterFenceTests
{
    /// <summary>The retired symbol family. A member or type spelled like this is the shim, restored.</summary>
    private const string RetiredWriter = "UpdateMemberPermissions";

    [Fact(DisplayName = "holds 362.A4: no retired member-permissions writer exists in the production assembly")]
    public void The_Retired_Writer_Is_Absent_From_The_Production_Assembly()
    {
        var assembly = typeof(IAdminTeamAccessAuthority).Assembly;

        var types = assembly.GetTypes()
            .Where(type => type.Name.Contains(RetiredWriter, StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var members = assembly.GetTypes()
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => member.Name.Contains(RetiredWriter, StringComparison.Ordinal))
                .Select(member => $"{type.FullName}.{member.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The narrowing surface IS present — without this the fence would pass on an assembly that lost
        // both writers, which is not the property ("exactly one", not "none").
        Assert.Contains(
            nameof(IAdminTeamAccessAuthority.NarrowMemberGrantAsync),
            typeof(IAdminTeamAccessAuthority).GetMethods().Select(method => method.Name));
        Assert.Empty(types);
        Assert.Empty(members);
    }

    [Fact(DisplayName = "holds 362.A4: no production source spells the retired member-permissions writer")]
    public void No_Production_Source_Spells_The_Retired_Writer()
    {
        var root = RepositoryRoot();
        var spellings = EnumerateProductionSource(root)
            .Where(file => Regex.IsMatch(
                CodeOnly(File.ReadAllText(file)), @"\b\w*" + RetiredWriter + @"\w*\b"))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(spellings);
    }

    private static readonly string[] ExcludedSegments =
        ["tests", "obj", "bin", "node_modules", ".git", ".claude", "artifacts"];

    // Relative-path matching: this repository can itself live under a directory named like one of these
    // (a git worktree under .claude/worktrees does), and an absolute match would exclude everything and
    // pass by finding nothing.
    private static IEnumerable<string> EnumerateProductionSource(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Strip comments so prose naming the retired symbol does not read as the symbol.</summary>
    private static string CodeOnly(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Program.cs")),
            $"Could not locate the node-host project root from '{thisFile}'.");
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(
            Directory.Exists(Path.Combine(repositoryRoot, "apps"))
                && Directory.Exists(Path.Combine(repositoryRoot, "packages")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
