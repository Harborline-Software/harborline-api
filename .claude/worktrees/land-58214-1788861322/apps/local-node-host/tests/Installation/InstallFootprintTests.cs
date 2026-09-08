using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Tests.Installation;

public sealed class InstallFootprintTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "harborline-install-footprint-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FourInstallFootprints_WriteOnlyTheirOwnFiles()
    {
        var identities = new[]
        {
            InstallIdentity.Parse("11111111111111111111111111111111"),
            InstallIdentity.Parse("22222222222222222222222222222222"),
            InstallIdentity.Parse("33333333333333333333333333333333"),
            InstallIdentity.Parse("44444444444444444444444444444444"),
        };
        string[] installNames = ["tenant1-dev", "tenant1-qa", "tenant2-dev", "tenant2-qa"];
        var providers = identities.Select(identity =>
            (IInstallFootprintProvider)new FileInstallFootprintProvider(
                new FixedInstallIdentityProvider(identity),
                _directory)).ToArray();
        var legacyDatabasePath = Path.Combine(_directory, "data", "sunfish.db");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDatabasePath)!);
        await File.WriteAllTextAsync(legacyDatabasePath, "existing-install-data");

        var footprints = await Task.WhenAll(providers.Select(provider =>
            provider.GetInstallFootprintAsync(CancellationToken.None).AsTask()));
        var defaultFootprint = Assert.Single(footprints, footprint => footprint.UsesLegacyPaths);

        var legacyDataDirectoryName = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? "LocalNode"
            : "local-node";
        Assert.Equal(Path.Combine(_directory, legacyDataDirectoryName), defaultFootprint.DataDirectory);
        Assert.Equal(legacyDatabasePath, defaultFootprint.DatabasePath);
        Assert.Equal("existing-install-data", await File.ReadAllTextAsync(defaultFootprint.DatabasePath));
        Assert.Equal(Path.Combine(_directory, "keys"), defaultFootprint.KeystoreDirectory);
        Assert.Equal(4, footprints.Select(footprint => footprint.DataDirectory).Distinct().Count());
        Assert.Equal(4, footprints.Select(footprint => footprint.DatabasePath).Distinct().Count());
        Assert.Equal(4, footprints.Select(footprint => footprint.KeystoreDirectory).Distinct().Count());

        await Task.WhenAll(footprints.Select((footprint, index) =>
            WriteFootprintAsync(footprint, installNames[index])));

        for (var index = 0; index < footprints.Length; index++)
        {
            var footprint = footprints[index];
            Assert.Equal(
                installNames[index],
                await File.ReadAllTextAsync(Path.Combine(footprint.DataDirectory, "node.txt")));
            Assert.Equal(installNames[index], await File.ReadAllTextAsync(footprint.DatabasePath));
            Assert.Equal(
                installNames[index],
                await File.ReadAllTextAsync(Path.Combine(footprint.KeystoreDirectory, "key.txt")));
        }
    }

    [Fact]
    public async Task InstallFootprintRegistration_DrivesDatabaseAndKeystorePathsFromOneIdentity()
    {
        var identityFilePath = Path.Combine(_directory, "installation", "install.identity");
        var services = new ServiceCollection();
        services.AddHarborlineInstallFootprint(_directory, identityFilePath);
        services.AddHarborlineEncryptedStore();

        await using var provider = services.BuildServiceProvider();
        var footprint = await provider.GetRequiredService<IInstallFootprintProvider>()
            .GetInstallFootprintAsync(CancellationToken.None);
        var encryption = provider.GetRequiredService<IOptions<EncryptionOptions>>().Value;

        Assert.Equal(Path.Combine(_directory, "data", "sunfish.db"), encryption.DatabasePath);
        Assert.Equal(footprint.DatabasePath, encryption.DatabasePath);
        Assert.Equal(Path.Combine(_directory, "keys"), footprint.KeystoreDirectory);
        Assert.Equal(
            await provider.GetRequiredService<IInstallIdentityProvider>()
                .GetInstallIdentityAsync(CancellationToken.None),
            footprint.Identity);
    }

    [Fact]
    public async Task SingleInstall_AdoptsExistingLegacyPathsEvenWhenTheirRootsDiffer()
    {
        var legacyDataDirectory = Path.Combine(_directory, "legacy-node-root", "local-node");
        var legacyDatabasePath = Path.Combine(_directory, "legacy-database-root", "sunfish.db");
        var legacyKeystoreDirectory = Path.Combine(_directory, "legacy-keystore-root");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDatabasePath)!);
        await File.WriteAllTextAsync(legacyDatabasePath, "known-existing-database");
        var provider = new FileInstallFootprintProvider(
            new FixedInstallIdentityProvider(
                InstallIdentity.Parse("cccccccccccccccccccccccccccccccc")),
            _directory,
            legacyDataDirectory,
            legacyDatabasePath,
            legacyKeystoreDirectory);

        var footprint = await provider.GetInstallFootprintAsync(CancellationToken.None);

        Assert.True(footprint.UsesLegacyPaths);
        Assert.Equal(legacyDataDirectory, footprint.DataDirectory);
        Assert.Equal(legacyDatabasePath, footprint.DatabasePath);
        Assert.Equal(legacyKeystoreDirectory, footprint.KeystoreDirectory);
        Assert.Equal("known-existing-database", await File.ReadAllTextAsync(footprint.DatabasePath));
    }

    [Fact]
    public async Task LocalNodeOptions_UsesFootprintDataDirectoryUnlessExplicitlyConfigured()
    {
        var legacyOwner = new FileInstallFootprintProvider(
            new FixedInstallIdentityProvider(InstallIdentity.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            _directory);
        _ = await legacyOwner.GetInstallFootprintAsync(CancellationToken.None);
        var provider = new FileInstallFootprintProvider(
            new FixedInstallIdentityProvider(InstallIdentity.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            _directory);
        var footprint = await provider.GetInstallFootprintAsync(CancellationToken.None);
        var defaults = new LocalNodeOptions();
        var explicitOptions = new LocalNodeOptions { DataDirectory = Path.Combine(_directory, "operator-choice") };

        defaults.ApplyInstallFootprint(footprint, dataDirectoryIsConfigured: false);
        explicitOptions.ApplyInstallFootprint(footprint, dataDirectoryIsConfigured: true);

        Assert.Equal(
            Path.Combine(
                _directory,
                "installs",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? "LocalNode"
                    : "local-node"),
            defaults.DataDirectory);
        Assert.Equal(Path.Combine(_directory, "operator-choice"), explicitOptions.DataDirectory);
    }

    [Fact]
    public async Task TeamContainer_UsesTheInstallScopedKeystoreDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outerServices = new ServiceCollection();
        outerServices.AddHarborlineInstallFootprint(
            _directory,
            Path.Combine(_directory, "installation", "install.identity"));
        await using var outerProvider = outerServices.BuildServiceProvider();
        var footprint = await outerProvider.GetRequiredService<IInstallFootprintProvider>()
            .GetInstallFootprintAsync(CancellationToken.None);
        var signer = new Ed25519Signer();
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        var registrar = DefaultTeamServiceRegistrar.Compose(
            footprint.DataDirectory,
            new TeamSubkeyDerivation(signer),
            new NodeIdentity("install-node", publicKey, privateKey),
            new SqlCipherKeyDerivation(),
            listenForPeers: false);
        var teamServices = new ServiceCollection();

        registrar(teamServices, new TeamId(Guid.Parse("10000000-0000-0000-0000-000000000047")), outerProvider);

        await using var teamProvider = teamServices.BuildServiceProvider();
        var keystore = Assert.IsType<WindowsDpapiKeystore>(teamProvider.GetRequiredService<IKeystore>());
        Assert.Equal(footprint.KeystoreDirectory, keystore.StorageDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task WriteFootprintAsync(InstallFootprint footprint, string value)
    {
        Directory.CreateDirectory(footprint.DataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(footprint.DatabasePath)!);
        Directory.CreateDirectory(footprint.KeystoreDirectory);
        await Task.WhenAll(
            File.WriteAllTextAsync(Path.Combine(footprint.DataDirectory, "node.txt"), value),
            File.WriteAllTextAsync(footprint.DatabasePath, value),
            File.WriteAllTextAsync(Path.Combine(footprint.KeystoreDirectory, "key.txt"), value));
    }

    private sealed class FixedInstallIdentityProvider(InstallIdentity identity) : IInstallIdentityProvider
    {
        public ValueTask<InstallIdentity> GetInstallIdentityAsync(CancellationToken ct) =>
            ValueTask.FromResult(identity);
    }
}
