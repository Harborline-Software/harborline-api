using System.Runtime.CompilerServices;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.TeamSwitching;

public sealed class TeamSwitcherDependencyTests
{
    [Fact]
    public void UiAdaptersBlazorProjectDoesNotReferenceKernelRuntime()
    {
        var project = File.ReadAllText(Path.Combine(
            ApiRepositoryRoot(),
            "packages",
            "ui-adapters-blazor",
            "Harborline.UIAdapters.Blazor.csproj"));

        Assert.DoesNotContain("kernel-runtime", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Harborline.Api.Kernel.Runtime", project, StringComparison.Ordinal);
    }

    private static string ApiRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "..",
            "..",
            ".."));
}
