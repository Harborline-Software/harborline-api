using Bunit;
using Harborline.Api.Foundation.Services;
using Harborline.Api.UIAdapters.Blazor.Components.Showcase;
using Harborline.Api.UICore.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Interop;

/// <summary>
/// Ticket 267: the panel's Copy-code button used to carry an inline
/// <c>onclick="return window.…copyCodeFromButton(this)"</c>. No JS module in this package — or in
/// any sibling repository — defined that global, so the button did nothing at every pin it shipped.
/// It now goes through the package's own clipboard module, and these tests are what makes that
/// claim checkable: they drive the REAL component through bUnit's JS interop and read back the
/// module URL and the payload it was handed.
/// </summary>
public sealed class ExamplePanelCopyCodeTests
{
    private const string SourceText = "<HarborlineButton>Press me</HarborlineButton>";

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(Substitute.For<IHarborlineCssProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineIconProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineThemeService>());
        return context;
    }

    private static IRenderedComponent<HarborlineExamplePanel> RenderWithSource(BunitContext context) =>
        context.Render<HarborlineExamplePanel>(parameters => parameters
            .Add(panel => panel.Title, "Button / Overview")
            .Add(panel => panel.Sources, new[]
            {
                new HarborlineSourceFile("Overview.razor", SourceText),
            }));

    private static void OpenCodeTab(IRenderedComponent<HarborlineExamplePanel> rendered) =>
        rendered.FindAll("button[role=tab]")
            .Single(tab => tab.TextContent.Contains("Code", StringComparison.Ordinal))
            .Click();

    [Fact(DisplayName = "The copy button hands the visible source to the package's own clipboard module")]
    public void CopyButton_WritesTheActiveSourceThroughTheClipboardModule()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var rendered = RenderWithSource(context);

        OpenCodeTab(rendered);
        rendered.Find("button.sf-example-panel-action").Click();

        // The module the panel imported must be one this package actually ships. A URL with no file
        // behind it is exactly the defect this ticket closes, so assert the file, not just the call.
        var importUrl = Assert.IsType<string>(
            Assert.Single(context.JSInterop.Invocations["import"].Last().Arguments));
        Assert.EndsWith("js/harborline-clipboard-download.js", importUrl, StringComparison.Ordinal);

        var write = Assert.Single(context.JSInterop.Invocations["writeText"]);
        Assert.Equal(SourceText, Assert.Single(write.Arguments));
        Assert.Contains("Copied", rendered.Find("button.sf-example-panel-action").TextContent, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A failing clipboard write leaves the button saying so rather than claiming success")]
    public void CopyButton_ReportsAFailedWrite()
    {
        using var context = CreateContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("writeText", _ => true).SetException(new JSException("clipboard denied"));
        var rendered = RenderWithSource(context);

        OpenCodeTab(rendered);
        rendered.Find("button.sf-example-panel-action").Click();

        var button = rendered.Find("button.sf-example-panel-action");
        Assert.Contains("Copy failed", button.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Copied", button.TextContent, StringComparison.Ordinal);
    }
}
