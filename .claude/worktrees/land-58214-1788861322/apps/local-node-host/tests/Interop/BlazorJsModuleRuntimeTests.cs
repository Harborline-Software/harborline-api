using System.Reflection;
using System.Text.RegularExpressions;
using Harborline.Api.UIAdapters.Blazor.FormFactor;
using Bunit;
using Microsoft.JSInterop;

namespace Harborline.Api.LocalNodeHost.Tests.Interop;

/// <summary>
/// Discovery over the ui-adapters-blazor sources: the JS module call sites and their module paths.
/// Both the identity fence and the runtime load test read their inventory from here, so neither
/// carries a hand-written list that can drift from the code.
/// </summary>
internal static class BlazorJsModuleSources
{
    /// <summary>Every <c>./_content/&lt;owner&gt;/</c> literal left in a .cs/.razor source, by file and line.</summary>
    /// <summary>Ticket 257: the <c>./</c> prefix is optional — a bare <c>"_content/Owner/"</c> literal evaded the fence.</summary>
    internal const string ContentUrlLiteralPattern = @"[""'](?:\./)?_content/(?<capture>[^/""'{]+)/";

    internal static IReadOnlyList<(string File, int Line, string Owner)> ContentUrlLiterals() =>
        Scan(ContentUrlLiteralPattern)
            .Select(row => (row.File, row.Line, row.Capture))
            .ToArray();

    /// <summary>
    /// Every module path a call site imports: the argument of <c>HarborlineJsModuleLoader.Resolve("…")</c>
    /// or <c>ImportAsync("…")</c>, and the tail of any surviving <c>./_content/&lt;owner&gt;/js/…</c> literal,
    /// so a site reverted to a literal URL is still part of the runtime inventory.
    /// </summary>
    internal static IReadOnlyList<(string File, int Line, string ModulePath)> ModulePaths() =>
        Scan(@"(?:(?:HarborlineJsModuleLoader\.Resolve|ImportAsync)\(\s*""|""\./_content/[^/""]+/)(?<capture>js/[^""]+\.js)""")
            .Select(row => (row.File, row.Line, row.Capture))
            .ToArray();

    private static IEnumerable<(string File, int Line, string Capture)> Scan(string pattern)
    {
        var package = LocatePackage();
        var regex = new Regex(pattern);
        foreach (var file in Directory.EnumerateFiles(package, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                (Path.GetExtension(file) is not ".cs" and not ".razor"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(package, file).Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in regex.Matches(lines[index]))
                {
                    yield return (relative, index + 1, match.Groups["capture"].Value);
                }
            }
        }
    }

    internal static string LocatePackage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "packages", "ui-adapters-blazor");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate packages/ui-adapters-blazor from the test output.");
    }

    /// <summary>The RCL's internal module loader, reached by reflection (internal, no IVT to this assembly).</summary>
    internal static Type LoaderType() =>
        typeof(FormFactorService).Assembly.GetType("Harborline.Api.UIAdapters.Blazor.Internal.Interop.HarborlineJsModuleLoader")
        ?? throw new InvalidOperationException("HarborlineJsModuleLoader not found in the Blazor adapter assembly.");

    internal static string Resolve(string modulePath) =>
        (string)LoaderType().GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { modulePath })!;
}

/// <summary>
/// Ticket 259: every JS module the Blazor adapter imports must resolve to a file that is actually
/// served from this assembly's static-web-asset path. The loader is driven for real; the JS runtime
/// is faked at the browser boundary only, and it resolves the import URL against the RCL's wwwroot
/// exactly as the browser would — a URL with no file behind it throws instead of 404ing silently.
/// </summary>
public sealed class BlazorJsModuleRuntimeTests
{
    /// <summary>
    /// Exact allow-list of module paths that are deliberately absent, by file, symbol and reason.
    /// Class: optional engine module, documented as not shipped, with a rendered fallback.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyAbsent = new(StringComparer.Ordinal)
    {
        ["js/map.js"] =
            "Components/Media/HarborlineMap.razor.cs HarborlineMap.JsModulePath — Leaflet-compatible engine " +
            "not shipped with the package (documented on the type); the component renders its preview " +
            "fallback when the import fails. harborline-map.js is the MapLibre engine for MapLibreAdapter " +
            "and exports a different contract (init/updateViewport), so it is not a substitute.",
    };

    [Fact(DisplayName = "Every discovered Blazor JS module import loads a real file from this assembly's static-web-asset path")]
    public async Task EveryDiscoveredModule_LoadsARealFile()
    {
        var discovered = BlazorJsModuleSources.ModulePaths();
        Assert.NotEmpty(discovered);

        // The REAL loader, driven through bUnit's JS interop: the URL it builds is recorded, then
        // resolved against the RCL's wwwroot the way the browser resolves a static web asset.
        var jsInterop = new BunitJSInterop { Mode = JSRuntimeMode.Loose };
        var loader = Activator.CreateInstance(
            BlazorJsModuleSources.LoaderType(),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { jsInterop.JSRuntime },
            culture: null)!;
        var importAsync = BlazorJsModuleSources.LoaderType().GetMethod("ImportAsync")!;

        var wwwroot = Path.Combine(BlazorJsModuleSources.LocatePackage(), "wwwroot");
        var prefix = BlazorJsModuleSources.Resolve(string.Empty);
        var failures = new List<string>();
        foreach (var (file, line, modulePath) in discovered.DistinctBy(row => row.ModulePath))
        {
            if (DeliberatelyAbsent.ContainsKey(modulePath)) continue;

            var pending = (ValueTask<IJSObjectReference>)importAsync
                .Invoke(loader, new object?[] { modulePath, CancellationToken.None })!;
            await pending;

            var url = Assert.IsType<string>(Assert.Single(jsInterop.Invocations["import"].Last().Arguments));
            Assert.StartsWith(prefix, url, StringComparison.Ordinal);
            var path = Path.Combine(wwwroot, url[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                failures.Add($"{file}:{line} → 404: {url} has no file at {path}.");
            }
        }

        Assert.Empty(failures);
    }

    [Fact(DisplayName = "The deliberately-absent module allow-list equals the discovered set of missing modules")]
    public void AbsentAllowList_EqualsTheDiscoveredMissingSet()
    {
        var wwwroot = Path.Combine(BlazorJsModuleSources.LocatePackage(), "wwwroot");
        var missing = BlazorJsModuleSources.ModulePaths()
            .Select(row => row.ModulePath)
            .Distinct(StringComparer.Ordinal)
            .Where(path => !File.Exists(Path.Combine(wwwroot, path.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(path => path, StringComparer.Ordinal);

        Assert.Equal(DeliberatelyAbsent.Keys.OrderBy(path => path, StringComparer.Ordinal), missing);
    }
}
