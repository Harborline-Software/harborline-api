using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class BootstrapAuthorityArchTests
{
    private static readonly string[] IssuerSymbols =
    [
        nameof(IBootstrapClaimIssuer),
        nameof(HostedControlPlaneBootstrapClaimIssuer),
        nameof(SelfHostedFileSystemOwnerBootstrapClaimIssuer),
        nameof(DesktopOsSessionBootstrapClaimIssuer),
    ];

    [Fact]
    public void Issuers_And_Redemption_Are_Not_Public_APIs()
    {
        Assert.True(typeof(IBootstrapClaimIssuer).IsNotPublic);
        Assert.True(typeof(HostedControlPlaneBootstrapClaimIssuer).IsNotPublic);
        Assert.True(typeof(SelfHostedFileSystemOwnerBootstrapClaimIssuer).IsNotPublic);
        Assert.True(typeof(DesktopOsSessionBootstrapClaimIssuer).IsNotPublic);
        Assert.True(typeof(BootstrapClaimRedemptionService).IsNotPublic);
    }

    [Fact]
    public void Only_Founder_Ceremony_Reaches_Issuance_And_Only_Redemption_Reaches_Installer_Mint()
    {
        var root = HostRoot();
        var production = ProductionSources(root).ToArray();
        Assert.Equal(
            [Path.Combine("Data", "Identity", "InstallationFounderBootstrapCeremony.cs")],
            Consumers(production, root, "BootstrapClaimRedemption.cs", IssuerSymbols));
        Assert.Equal(
            [Path.Combine("Data", "Identity", "BootstrapClaimRedemption.cs")],
            Consumers(production, root, "InitialGrantIssuanceService.cs", ["PrepareBootstrapGrant"]));

        var installerConstructors = production
            .Where(path => ReadIfPresent(path).Contains("GranterKind.Installer", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [Path.Combine("Data", "Identity", "InitialGrantIssuanceService.cs")],
            installerConstructors);
        Assert.DoesNotContain("IssueBootstrapWithEpochAsync", File.ReadAllText(
            Path.Combine(root, "Data", "Identity", "InitialGrantIssuanceService.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_Fence_Bites_On_Planted_Offenders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bootstrap-arch-{Guid.NewGuid():N}");
        try
        {
            Write(root, "BootstrapClaimRedemption.cs", "interface IBootstrapClaimIssuer { } class DesktopOsSessionBootstrapClaimIssuer { }");
            Write(root, "InitialGrantIssuanceService.cs", "void PrepareBootstrapGrant() { var k = GranterKind.Installer; }");
            Write(root, "InstallationFounderBootstrapCeremony.cs", "IBootstrapClaimIssuer issuer; DesktopOsSessionBootstrapClaimIssuer desktop;");
            Write(root, "SmuggledRoute.cs", "IBootstrapClaimIssuer issuer; issuer.IssueAsync(); PrepareBootstrapGrant(); var k = GranterKind.Installer;");
            var production = ProductionSources(root).ToArray();

            Assert.Contains("SmuggledRoute.cs",
                Consumers(production, root, "BootstrapClaimRedemption.cs", IssuerSymbols));
            Assert.Contains("SmuggledRoute.cs",
                Consumers(production, root, "InitialGrantIssuanceService.cs", ["PrepareBootstrapGrant"]));
            Assert.Equal(2, production.Count(path =>
                File.ReadAllText(path).Contains("GranterKind.Installer", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Consumers(
        IEnumerable<string> sources,
        string root,
        string declarationFile,
        IReadOnlyList<string> symbols) =>
        sources
            .Where(path => !string.Equals(Path.GetFileName(path), declarationFile, StringComparison.Ordinal))
            .Where(path => symbols.Any(symbol =>
                ReadIfPresent(path).Contains(symbol, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ReadIfPresent(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            // Other architecture bite-proofs temporarily add and revert a production-surface file.
            return string.Empty;
        }
    }

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "tests" or "obj" or "bin"));

    private static void Write(string root, string relativePath, string text)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string HostRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host", "Program.cs");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate local-node-host.");
    }
}
