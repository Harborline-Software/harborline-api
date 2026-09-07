using System.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Tests.Localization;

public sealed class HarborlineXliffTargetTests
{
    [Fact]
    public void Export_and_import_targets_execute_against_a_real_localized_project()
    {
        var root = FindRepoRoot();
        var project = Path.Combine(root, "packages", "foundation", "Harborline.Foundation.csproj");
        var output = Path.Combine(Path.GetTempPath(), $"ticket-251-xliff-{Guid.NewGuid():N}");

        try
        {
            RunTarget(project, "HarborlineExportXliff", output);
            Assert.True(File.Exists(Path.Combine(output, "xliff", "SharedResource.ar-SA.xlf")));

            RunTarget(project, "HarborlineImportXliff", output);
            Assert.Contains(
                Directory.EnumerateFiles(Path.Combine(output, "obj"), "*.resx", SearchOption.AllDirectories),
                path => Path.GetFileName(path).StartsWith("SharedResource", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static void RunTarget(string project, string target, string output)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add($"-t:{target}");
        startInfo.ArgumentList.Add($"-p:HarborlineXliffDirectory={Path.Combine(output, "xliff")}");
        startInfo.ArgumentList.Add($"-p:IntermediateOutputPath={Path.Combine(output, "obj")}{Path.DirectorySeparatorChar}");

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{target} failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("repo root (Harborline.Api.slnx) not found above " + AppContext.BaseDirectory);
    }
}
