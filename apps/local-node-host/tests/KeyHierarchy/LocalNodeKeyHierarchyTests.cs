using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.KeyHierarchy;

public sealed class LocalNodeKeyHierarchyTests
{
    [Fact]
    public void Sc4StoreDek_ExtendsRecoverableCustodyToPerTeamStores()
    {
        var rootSeed = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var storeDek = Convert.FromHexString(
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");
        var options = new LocalNodeOptions
        {
            StoreDekHex = Convert.ToHexString(storeDek),
            MultiTeam = new MultiTeamOptions { Enabled = true },
        };

        var hierarchy = LocalNodeKeyHierarchy.Resolve(options, rootSeed);

        Assert.Equal(rootSeed, hierarchy.IdentityRootKey.ToArray());
        Assert.Equal(storeDek, hierarchy.AtRestRootKey.ToArray());
        Assert.True(hierarchy.PerTeamKvStoreIsEnvelopeExtended);
        Sc4RecoverabilityGuard.Validate(
            options,
            hierarchy.PerTeamKvStoreIsEnvelopeExtended);
    }

    [Fact]
    public void Sc4Guard_UsesResolvedHierarchyInsteadOfCallerAssertion()
    {
        var rootSeed = new byte[32];
        var options = new LocalNodeOptions
        {
            StoreDekHex = new string('A', 64),
            MultiTeam = new MultiTeamOptions { Enabled = true },
        };
        var hierarchy = LocalNodeKeyHierarchy.Resolve(options, rootSeed);

        var exception = Record.Exception(() =>
            Sc4RecoverabilityGuard.Validate(options, hierarchy));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TeamStoreActivation_RekeysLegacySeedKeyToRecoverableAtRestRoot()
    {
        var teamId = new TeamId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var legacyRoot = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var atRestRoot = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var legacyKey = Convert.FromHexString(
            "062A8D775B7FC8CA7D8FC1963FE0BA495D79E346B18B34443FA2452081F42C21");
        var newKey = Convert.FromHexString(
            "452E122E1BEC872125D5CDC9298A2B6240810C5E112A5BE67B0BF5C5252FA623");
        var store = new LegacyKeyEncryptedStore(legacyKey);
        await using var factory = new TeamContextFactory((services, _, _) =>
        {
            services.AddSingleton<IEncryptedStore>(store);
            services.AddSingleton(Options.Create(new EncryptionOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), "ticket-043-legacy-key.db"),
            }));
        }, TimeProvider.System);
        _ = await factory.GetOrCreateAsync(teamId, "Ticket 043", CancellationToken.None);
        var activator = new TeamStoreActivator(
            factory,
            new SqlCipherKeyDerivation(),
            atRestRoot,
            legacyRoot);

        await activator.ActivateAsync(teamId, CancellationToken.None);

        Assert.Equal([newKey, legacyKey], store.OpenAttempts);
        Assert.Equal(newKey, store.RotatedTo);
    }

    [Fact]
    public async Task DeriveReplacementKeys_CoversEveryRootDerivedDomain()
    {
        var replacementRoot = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var keys = await LocalNodeDerivedKeyRekeyer.DeriveReplacementKeysAsync(
            replacementRoot,
            "11111111-1111-1111-1111-111111111111",
            new TenantId("tenant-043"),
            new SubjectId("subject-043"),
            "encrypted-field-aes",
            new InMemorySubjectErasureRegistry(),
            CancellationToken.None);

        Assert.Equal(replacementRoot, keys.RootIdentitySeed);
        Assert.Equal("9AA733991B5FDF0AFB636007DB1CBD7373A3C8DCB5E093FC400AD041BC303598", Convert.ToHexString(keys.TeamSigningSeed));
        Assert.Equal("452E122E1BEC872125D5CDC9298A2B6240810C5E112A5BE67B0BF5C5252FA623", Convert.ToHexString(keys.SqlCipherKey));
        Assert.Equal("A0C34400AACEFEC4F95CB3065195A4B5F193556EB4A00837AF590F2358A6EB40", Convert.ToHexString(keys.RecoveryX25519PrivateKey));
        Assert.Equal("3D2A6514A458EBC9C1B7BE2462FF3609770A135ED57118B6582013344E670EB4", Convert.ToHexString(keys.DmX25519PrivateKey));
        Assert.Equal("74675DD81A0F4C5FBB52E48AF17F4A4A05B5753B2A803004C8318CBDEE04F855", Convert.ToHexString(keys.XWingPrivateKeySeed));
        Assert.Equal("7856CC7236FCBD57688C2160CA51304FB2E11A96D34BB5E0773996B95E79D1CC", Convert.ToHexString(keys.TenantDek));
        Assert.Equal("31B330D8DEA50DA58AA2A9A53FE3108BB6ECAB441DCDC14238F37B6B6B4D5632", Convert.ToHexString(keys.SubjectDek));
    }

    private sealed class LegacyKeyEncryptedStore(byte[] legacyKey) : IEncryptedStore
    {
        public List<byte[]> OpenAttempts { get; } = [];

        public byte[]? RotatedTo { get; private set; }

        public Task OpenAsync(string databasePath, ReadOnlyMemory<byte> key, CancellationToken ct)
        {
            var attempted = key.ToArray();
            OpenAttempts.Add(attempted);
            if (!attempted.SequenceEqual(legacyKey))
            {
                throw new InvalidKeyException();
            }
            return Task.CompletedTask;
        }

        public Task RotateKeyAsync(ReadOnlyMemory<byte> newKey, CancellationToken ct)
        {
            RotatedTo = newKey.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task CloseAsync() => Task.CompletedTask;
    }
}
