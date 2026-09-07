using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 094 — the Forms family answers ONE error envelope, and exception text never reaches it.
/// </summary>
/// <remarks>
/// <para>
/// Ticket 092 moved reports, views and data-exchange to <c>{"code": "&lt;machine.code&gt;"}</c>; Forms was
/// carved out and kept answering English prose, including one route that returned
/// <c>new { error = ex.Message }</c> — whatever the throwing code happened to write, which can carry
/// internal type names, paths or state the caller was never meant to see, and which changes without
/// notice whenever the implementation does.
/// </para>
/// <para>
/// The envelope is <c>{ "code": "&lt;machine.code&gt;", "detail": { ... } }</c>: a stable code the client
/// localizes, plus named detail fields for the parameterized diagnostics (which header was duplicated,
/// what the limit was), so nothing a caller needs is lost with the prose.
/// </para>
/// <para>
/// The inventory is discovered BY SYMBOL: the Forms route registrars are found by reflection over the
/// host assembly, and each is mapped to its source file. A new Forms route class therefore lands inside
/// the fence without anyone remembering to add it. WHAT THIS DOES NOT CATCH, stated rather than implied:
/// the body check is a source-text scan of the argument to each <c>Results.*</c> call, so an error body
/// assembled in a local variable, in a helper in another file, or by a DTO whose members are computed
/// elsewhere passes. It pins the shapes a contributor actually writes at a route.
/// </para>
/// </remarks>
public sealed class FormsErrorEnvelopeArchTests
{
    /// <summary>
    /// Exception members that would put implementation-authored text (or a stack) on the wire. This is the
    /// inventory of forbidden shapes the scan searches for, not a list of excused call sites.
    /// </summary>
    private static readonly string[] ForbiddenExceptionMembers =
        ["Message", "StackTrace", "InnerException", "TargetSite", "Source", "Data", "ToString"];

    [Fact(DisplayName = "094: every Forms route registrar is discovered by symbol and has a source file")]
    public void Forms_Route_Registrars_Are_Discovered_By_Symbol()
    {
        var files = DiscoveredFormsRouteSourceFiles();

        // Three registrars today (FormsRoutes, FormDefinitionRoutes, FormDraftRoutes). A fourth one must
        // arrive here — this is the fence's inventory, so a Forms route file that is not found is a route
        // family the body scan below never reads.
        Assert.Equal(
            ["FormDefinitionRoutes.cs", "FormDraftRoutes.cs", "FormsRoutes.cs"],
            files.Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact(DisplayName = "094: no exception message or stack reaches a Forms response body")]
    public void No_Exception_Text_Reaches_A_Forms_Response_Body()
    {
        var offenders = new List<string>();
        foreach (var file in DiscoveredFormsRouteSourceFiles())
        {
            foreach (var (line, body) in ResultBodies(file))
            {
                foreach (var member in ForbiddenExceptionMembers)
                {
                    // `ex.Message`, `exception.StackTrace`, `ex.InnerException.Message`, `ex.ToString()`.
                    if (Regex.IsMatch(
                        body,
                        @"\b\w*(ex|exception|error)\w*\s*\.\s*" + member + @"\b",
                        RegexOptions.IgnoreCase))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{line} -> .{member}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact(DisplayName = "094: every Forms response body is the code envelope — never an `error` prose key")]
    public void Every_Forms_Response_Body_Is_The_Code_Envelope()
    {
        var offenders = new List<string>();
        foreach (var file in DiscoveredFormsRouteSourceFiles())
        {
            foreach (var (line, body) in ResultBodies(file))
            {
                // An anonymous body carrying `error = ...` is the retired prose envelope. `code = ...` is
                // the replacement; `detail = new { ... }` carries the parameters the prose used to.
                if (Regex.IsMatch(body, @"\bnew\s*\{[^}]*\berror\s*="))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{line}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The Forms route registrars, by symbol: static classes in the host assembly whose names name the
    /// family and which register routes (a <c>Map*</c> method).
    /// </summary>
    private static IReadOnlyList<string> DiscoveredFormsRouteSourceFiles()
    {
        var healthDirectory = HealthDirectory();
        var registrars = typeof(Program).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: true, IsSealed: true }) // static class
            .Where(type => type.Name.StartsWith("Form", StringComparison.Ordinal)
                && type.Name.EndsWith("Routes", StringComparison.Ordinal))
            .Where(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(method => method.Name.StartsWith("Map", StringComparison.Ordinal)))
            .Select(type => Path.Combine(healthDirectory, type.Name + ".cs"))
            .ToArray();

        Assert.NotEmpty(registrars);
        foreach (var file in registrars)
        {
            Assert.True(File.Exists(file), $"Route registrar source not found: {file}");
        }

        return registrars;
    }

    /// <summary>
    /// Every <c>Results.Xxx(...)</c> argument text in a file, with its 1-based line, comments stripped.
    /// Bracket-matched rather than regexed so a nested <c>new { ... }</c> is captured whole.
    /// </summary>
    private static IEnumerable<(int Line, string Body)> ResultBodies(string file)
    {
        var source = CodeOnly(File.ReadAllText(file));
        foreach (Match call in Regex.Matches(source, @"\bResults\s*\.\s*\w+\s*\("))
        {
            var open = call.Index + call.Length - 1;
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] is '(' or '{')
                {
                    depth++;
                }
                else if (source[i] is ')' or '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return (
                            source.Take(call.Index).Count(c => c == '\n') + 1,
                            source[(open + 1)..i]);
                        break;
                    }
                }
            }
        }
    }

    private static string CodeOnly(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string HealthDirectory([CallerFilePath] string thisFile = "")
    {
        var archTestsDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(archTestsDir)!;
        var hostRoot = Path.GetDirectoryName(testsDir)!;
        var health = Path.Combine(hostRoot, "Health");
        Assert.True(Directory.Exists(health), $"Could not locate the node-host Health directory from '{thisFile}'.");
        return health;
    }
}
