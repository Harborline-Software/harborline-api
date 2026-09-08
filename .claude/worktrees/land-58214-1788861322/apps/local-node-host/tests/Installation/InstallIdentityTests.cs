using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.DependencyInjection;
using Harborline.Api.Kernel.Security.Keys;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Installation;

public sealed class InstallIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "harborline-install-identity-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FourInstallIdentityFiles_AreUniqueAndStableAcrossRestart()
    {
        string[] installNames = ["tenant1-dev", "tenant1-qa", "tenant2-dev", "tenant2-qa"];

        var firstLaunch = await Task.WhenAll(installNames.Select(GetIdentityAsync));
        var restarted = await Task.WhenAll(installNames.Select(GetIdentityAsync));

        Assert.Equal(4, firstLaunch.Distinct().Count());
        Assert.Equal(firstLaunch, restarted);
        Assert.All(firstLaunch, identity =>
            Assert.Matches("^[0-9a-f]{32}$", identity.Value));
    }

    [Fact]
    public async Task FourInstalls_SharingOneOsKeystore_HaveDistinctSeedsAndDerivedKeysAcrossBothAxes()
    {
        string[] installNames = ["tenant1-dev", "tenant1-qa", "tenant2-dev", "tenant2-qa"];
        var keystore = new InMemoryKeystore();
        var identities = await Task.WhenAll(installNames.Select(GetIdentityAsync));
        var seeds = await Task.WhenAll(installNames.Select(async installName =>
        {
            IInstallIdentityProvider identityProvider = new FileInstallIdentityProvider(
                Path.Combine(_directory, installName, "install.identity"));
            IRootSeedProvider seedProvider = new KeystoreRootSeedProvider(keystore, identityProvider);
            return (await seedProvider.GetRootSeedAsync(CancellationToken.None)).ToArray();
        }));

        for (var index = 0; index < identities.Length; index++)
        {
            var stored = await keystore.GetKeyAsync(
                $"sunfish:root-seed:v1:{identities[index].Value}",
                CancellationToken.None);
            Assert.True(stored.HasValue);
            Assert.Equal(seeds[index], stored.Value.ToArray());
        }

        var subkeyDerivation = new TeamSubkeyDerivation(new Ed25519Signer());
        var sqlCipherDerivation = new SqlCipherKeyDerivation();
        var teamSubkeys = seeds.Select(seed =>
            subkeyDerivation.DeriveSubkey(seed, "shared-team-id")).ToArray();
        var databaseKeys = seeds.Select(seed =>
            sqlCipherDerivation.DeriveSqlCipherKey(seed, "shared-store-id")).ToArray();

        AssertDistinctAcrossMatrix(seeds);
        AssertDistinctAcrossMatrix(teamSubkeys);
        AssertDistinctAcrossMatrix(databaseKeys);
    }

    [Fact]
    public async Task ExistingLegacySeed_IsAdoptedByTheNamespacedSlot()
    {
        byte[] legacySeed =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
        ];
        var keystore = new InMemoryKeystore();
        await keystore.SetKeyAsync(
            KeystoreRootSeedProvider.LegacySlotName,
            legacySeed,
            CancellationToken.None);
        IInstallIdentityProvider identityProvider = new FileInstallIdentityProvider(
            Path.Combine(_directory, "legacy-install", "install.identity"));
        var identity = await identityProvider.GetInstallIdentityAsync(CancellationToken.None);
        IRootSeedProvider seedProvider = new KeystoreRootSeedProvider(keystore, identityProvider);

        var adopted = await seedProvider.GetRootSeedAsync(CancellationToken.None);

        Assert.Equal(legacySeed, adopted.ToArray());
        var namespaced = await keystore.GetKeyAsync(
            $"sunfish:root-seed:v1:{identity.Value}",
            CancellationToken.None);
        Assert.Equal(legacySeed, namespaced?.ToArray());
        Assert.Null(await keystore.GetKeyAsync(
            KeystoreRootSeedProvider.LegacySlotName,
            CancellationToken.None));
    }

    [Fact]
    public async Task RootSeedRegistration_ExposesOneDurableIdentityAcrossRestart()
    {
        var identityFilePath = Path.Combine(_directory, "composed", "install.identity");
        var keystore = new InMemoryKeystore();

        var firstServices = new ServiceCollection();
        firstServices.AddSingleton<IKeystore>(keystore);
        firstServices.AddHarborlineRootSeedProvider(installIdentityFilePath: identityFilePath);
        await using var firstProvider = firstServices.BuildServiceProvider();
        var firstIdentity = await firstProvider.GetRequiredService<IInstallIdentityProvider>()
            .GetInstallIdentityAsync(CancellationToken.None);
        var firstSeed = await firstProvider.GetRequiredService<IRootSeedProvider>()
            .GetRootSeedAsync(CancellationToken.None);

        var restartedServices = new ServiceCollection();
        restartedServices.AddSingleton<IKeystore>(keystore);
        restartedServices.AddHarborlineRootSeedProvider(installIdentityFilePath: identityFilePath);
        await using var restartedProvider = restartedServices.BuildServiceProvider();
        var restartedIdentity = await restartedProvider.GetRequiredService<IInstallIdentityProvider>()
            .GetInstallIdentityAsync(CancellationToken.None);
        var restartedSeed = await restartedProvider.GetRequiredService<IRootSeedProvider>()
            .GetRootSeedAsync(CancellationToken.None);

        Assert.Equal(firstIdentity, restartedIdentity);
        Assert.Equal(firstSeed.ToArray(), restartedSeed.ToArray());
    }

    [Fact]
    public async Task ConcurrentFirstLaunchers_ObserveOneInstallIdentity()
    {
        var identityFilePath = Path.Combine(_directory, "concurrent", "install.identity");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var launches = Enumerable.Range(0, 16).Select(async _ =>
        {
            IInstallIdentityProvider provider = new FileInstallIdentityProvider(identityFilePath);
            await start.Task;
            return await provider.GetInstallIdentityAsync(CancellationToken.None);
        }).ToArray();

        start.SetResult();
        var identities = await Task.WhenAll(launches);

        Assert.Single(identities.Distinct());
    }

    [Fact]
    public async Task ConcurrentInstallUpgrades_AllowOnlyOneLegacySeedAdopter()
    {
        byte[] legacySeed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var keystore = new CoordinatedMigrationKeystore(legacySeed, expectedLegacyReaders: 4);
        var providers = Enumerable.Range(0, 4).Select(index =>
        {
            IInstallIdentityProvider identityProvider = new FileInstallIdentityProvider(
                Path.Combine(_directory, "migration-race-" + index, "install.identity"));
            return (IRootSeedProvider)new KeystoreRootSeedProvider(keystore, identityProvider);
        }).ToArray();

        var seeds = await Task.WhenAll(providers.Select(async provider =>
            (await provider.GetRootSeedAsync(CancellationToken.None)).ToArray()));

        Assert.Equal(4, seeds.Select(Convert.ToHexString).Distinct().Count());
        Assert.Single(seeds, seed => seed.SequenceEqual(legacySeed));
        Assert.Null(await keystore.GetCurrentAsync(
            KeystoreRootSeedProvider.LegacySlotName,
            CancellationToken.None));
    }

    [Fact]
    public async Task WrongLengthLegacySeed_IsNotAdopted()
    {
        var keystore = new InMemoryKeystore();
        await keystore.SetKeyAsync(
            KeystoreRootSeedProvider.LegacySlotName,
            new byte[] { 0x01, 0x02, 0x03 },
            CancellationToken.None);
        IInstallIdentityProvider identityProvider = new FileInstallIdentityProvider(
            Path.Combine(_directory, "corrupt-legacy", "install.identity"));
        var identity = await identityProvider.GetInstallIdentityAsync(CancellationToken.None);
        IRootSeedProvider seedProvider = new KeystoreRootSeedProvider(keystore, identityProvider);

        var seed = await seedProvider.GetRootSeedAsync(CancellationToken.None);

        Assert.Equal(KeystoreRootSeedProvider.SeedLength, seed.Length);
        var namespaced = await keystore.GetKeyAsync(
            $"sunfish:root-seed:v1:{identity.Value}",
            CancellationToken.None);
        Assert.Equal(seed.ToArray(), namespaced?.ToArray());
    }

    private static void AssertDistinctAcrossMatrix(IReadOnlyList<byte[]> values)
    {
        Assert.Equal(4, values.Select(Convert.ToHexString).Distinct().Count());

        Assert.NotEqual(values[0], values[1]); // tenant1: dev vs qa
        Assert.NotEqual(values[2], values[3]); // tenant2: dev vs qa
        Assert.NotEqual(values[0], values[2]); // dev: tenant1 vs tenant2
        Assert.NotEqual(values[1], values[3]); // qa: tenant1 vs tenant2
    }

    private async Task<InstallIdentity> GetIdentityAsync(string installName)
    {
        IInstallIdentityProvider provider = new FileInstallIdentityProvider(
            Path.Combine(_directory, installName, "install.identity"));

        return await provider.GetInstallIdentityAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CoordinatedMigrationKeystore : IAtomicKeystore
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private readonly int _expectedLegacyReaders;
        private readonly TaskCompletionSource _legacyReadersReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _legacyReaderCount;

        public CoordinatedMigrationKeystore(byte[] legacySeed, int expectedLegacyReaders)
        {
            _keys[KeystoreRootSeedProvider.LegacySlotName] = legacySeed.ToArray();
            _expectedLegacyReaders = expectedLegacyReaders;
        }

        public async Task<ReadOnlyMemory<byte>?> GetKeyAsync(string name, CancellationToken ct)
        {
            byte[]? value;
            lock (_keys)
            {
                value = _keys.TryGetValue(name, out var stored) ? stored.ToArray() : null;
            }

            if (name == KeystoreRootSeedProvider.LegacySlotName && value is not null)
            {
                if (Interlocked.Increment(ref _legacyReaderCount) == _expectedLegacyReaders)
                {
                    _legacyReadersReady.SetResult();
                }

                await _legacyReadersReady.Task.WaitAsync(ct);
            }

            return value;
        }

        public Task SetKeyAsync(string name, ReadOnlyMemory<byte> key, CancellationToken ct)
        {
            lock (_keys)
            {
                _keys[name] = key.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task DeleteKeyAsync(string name, CancellationToken ct)
        {
            lock (_keys)
            {
                _keys.Remove(name);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryMoveKeyAsync(
            string sourceName,
            string destinationName,
            CancellationToken ct)
        {
            lock (_keys)
            {
                if (_keys.ContainsKey(destinationName)
                    || !_keys.Remove(sourceName, out var value))
                {
                    return Task.FromResult(false);
                }

                _keys[destinationName] = value;
                return Task.FromResult(true);
            }
        }

        public Task<ReadOnlyMemory<byte>?> GetCurrentAsync(string name, CancellationToken ct)
        {
            lock (_keys)
            {
                return Task.FromResult<ReadOnlyMemory<byte>?>(
                    _keys.TryGetValue(name, out var value)
                        ? value.ToArray()
                        : (ReadOnlyMemory<byte>?)null);
            }
        }
    }
}
