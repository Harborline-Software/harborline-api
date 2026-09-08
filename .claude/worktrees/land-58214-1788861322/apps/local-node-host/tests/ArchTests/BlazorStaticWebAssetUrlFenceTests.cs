using Harborline.Api.LocalNodeHost.Tests.Interop;
using Harborline.Api.UIAdapters.Blazor.FormFactor;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// The Blazor RCL serves its JS modules from <c>_content/&lt;AssemblyName&gt;/</c>. Ticket 259 removed
/// the last hand-written <c>_content/…</c> literal: every import now goes through
/// <c>HarborlineJsModuleLoader.Resolve</c>, which builds the prefix from the EMITTED assembly name. This
/// fence keeps it that way — a planted literal URL (broken owner or not) is red, and the derived prefix
/// is checked against the assembly identity discovered from the built assembly.
/// </summary>
public sealed class BlazorStaticWebAssetUrlFenceTests
{
    /// <summary>
    /// Exact allow-list of foreign static-web-asset owners referenced from this RCL's sources.
    /// Each row is a URL whose owner is a different razor class library, so it must NOT follow
    /// this assembly's identity and may stay a literal.
    /// </summary>
    private static readonly Dictionary<string, string> ForeignAssetOwners = new(StringComparer.Ordinal)
    {
        // Icon sprite RCL; packages/ui-adapters-blazor/Icons/** is excluded from this project.
        ["Harborline.Icons"] = "separate icon RCL, not this assembly",
    };

    [Fact(DisplayName = "No own-RCL _content URL literal survives in ui-adapters-blazor; imports go through the loader")]
    public void OwnContentUrls_AreNotLiterals()
    {
        var literals = BlazorJsModuleSources.ContentUrlLiterals()
            .Where(row => !ForeignAssetOwners.ContainsKey(row.Owner))
            .Select(row => $"{row.File}:{row.Line} → _content/{row.Owner}/")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(literals);
    }

    [Fact(DisplayName = "The loader's content prefix is the emitted AssemblyName")]
    public void LoaderPrefix_IsTheEmittedAssemblyName()
    {
        var expected = typeof(FormFactorService).Assembly.GetName().Name;
        Assert.NotNull(expected);

        Assert.Equal($"./_content/{expected}/js/harborline-a11y.js", BlazorJsModuleSources.Resolve("js/harborline-a11y.js"));
    }

    internal static IReadOnlyCollection<string> ForeignAssetOwnerRows => ForeignAssetOwners.Keys;

    /// <summary>Ticket 257: every owner the shared discovery finds in the RCL, for the vacuous-row property.</summary>
    internal static string[] DiscoveredContentUrlOwners() =>
        BlazorJsModuleSources.ContentUrlLiterals()
            .Select(row => row.Owner)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact(DisplayName = "Ticket 257 evasion: a bare \"_content/Owner/\" literal (no \"./\") is discovered by the shared pattern")]
    public void ContentUrlPatternMatchesABareLiteralWithoutTheDotSlashPrefix()
    {
        var pattern = new System.Text.RegularExpressions.Regex(BlazorJsModuleSources.ContentUrlLiteralPattern);
        Assert.Equal("Planted.Rcl", pattern.Match("const string Url = \"_content/Planted.Rcl/js/module.js\";").Groups["capture"].Value);
        Assert.Equal("Planted.Rcl", pattern.Match("const string Url = \"./_content/Planted.Rcl/js/other.js\";").Groups["capture"].Value);
    }
}
