using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

/// <summary>
/// Ticket 063 ruling 2: the operator CLI's package/namespace identity is Harborline.Api.NodeOperatorCli, but the
/// installed command stays <c>harborline-node</c>. The SDK names the apphost, deps.json and runtimeconfig after
/// AssemblyName, so the ruling is only true if AssemblyName evaluates to the command name. This asks MSBuild, not
/// the XML, so a Directory.Build.props override would be caught too.
/// </summary>
public sealed class OperatorCliCommandNameTests
{
    [Fact]
    public void Published_command_is_harborline_node_and_identity_is_the_api_namespace()
    {
        var root = FindRepoRoot();
        var csproj = Path.Combine(root, "apps", "node-operator-cli", "Harborline.NodeOperatorCli.csproj");
        Assert.True(File.Exists(csproj), csproj);
        var psi = new ProcessStartInfo("dotnet",
            $"msbuild \"{csproj}\" -getProperty:AssemblyName -getProperty:PackageId -getProperty:RootNamespace -getProperty:TargetFileName -p:Configuration=Release")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stdout + process.StandardError.ReadToEnd());
        var properties = JsonDocument.Parse(stdout).RootElement.GetProperty("Properties");
        Assert.Equal("harborline-node", properties.GetProperty("AssemblyName").GetString());
        Assert.Equal("harborline-node.dll", properties.GetProperty("TargetFileName").GetString());
        Assert.Equal("Harborline.Api.NodeOperatorCli", properties.GetProperty("PackageId").GetString());
        Assert.Equal("Harborline.Api.NodeOperatorCli", properties.GetProperty("RootNamespace").GetString());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repo root (Harborline.Api.slnx) not found above " + AppContext.BaseDirectory);
    }
}
