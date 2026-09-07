using System.Diagnostics;

using Harborline.Api.Foundation.Integrations.DependencyInjection;
using Harborline.Api.Foundation.PasswordHashing.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection("Harborline process environment")]
public sealed class HarborlineOperationalEnvironmentTests : IDisposable
{
    private const string LegacyPrefix = "SUNFISH" + "_";
    private const string ReplacementPrefix = "HARBORLINE_";

    private static readonly string[] LegacyNames =
    [
        LegacyPrefix + "ALLOW_MOCK_PASSWORD_HASHER",
        LegacyPrefix + "ALLOW_MOCK_PROVIDERS",
        LegacyPrefix + "COMMS_DM_DISABLED",
        LegacyPrefix + "KG_VEC0_NATIVE",
        LegacyPrefix + "KG_VEC0_REAL",
        LegacyPrefix + "PQC_HYBRID_WRITE_DISABLED",
    ];

    private readonly Dictionary<string, string?> _savedLegacyEnvironment;

    public HarborlineOperationalEnvironmentTests()
    {
        _savedLegacyEnvironment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string?>((string)entry.Key, (string?)entry.Value))
            .Where(entry => entry.Key.StartsWith(LegacyPrefix, StringComparison.Ordinal) ||
                            entry.Key.StartsWith("CARRIER" + "_", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        foreach (var name in _savedLegacyEnvironment.Keys)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public static TheoryData<string> LegacyFlagNames => new(LegacyNames);

    [Theory]
    [MemberData(nameof(LegacyFlagNames))]
    public void Every_reader_refuses_every_legacy_flag_with_the_exact_replacement(string legacyName)
    {
        Environment.SetEnvironmentVariable(legacyName, "1");
        try
        {
            foreach (var (readerName, read) in Readers(legacyName))
            {
                var error = Assert.Throws<InvalidOperationException>(read);
                Assert.Equal(Diagnostic(legacyName), error.Message);
                Assert.NotEmpty(readerName);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(legacyName, null);
        }
    }

    [Fact]
    public async Task Real_composition_refuses_an_inherited_legacy_flag_before_provider_creation()
    {
        var legacyName = LegacyPrefix + "COMMS_DM_DISABLED";
        var root = Path.Combine(Path.GetTempPath(), $"ticket-251-composition-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(legacyName, "1");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--LocalNode:RootSeedHex=" + new string('2', 64),
                        "--LocalNode:WebClient:Enabled=false",
                        "--LocalNode:MultiTeam:Enabled=false",
                    ],
                    sessionTokenOverride: "ticket-251-composition",
                    dataDirectory: root,
                    finalServiceProviderProbe: (_, _) => throw new UnreachableException(),
                    installFootprintRootOverride: root));

            Assert.Equal(Diagnostic(legacyName), error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(legacyName, null);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<(string Name, Action Read)> Readers(string legacyName) =>
    [
        ("DM kill-switch", () => _ = new CommsDmFeatureFlag()),
        ("hybrid-write kill-switch", () => new ServiceCollection().AddNodeHybridKemWritePolicy(false)),
        ("vec0 opt-in", () => _ = Vec0Native.RealVecOptedIn()),
        ("vec0 path", () => _ = Vec0Native.ResolveNativePath()),
        ("mock provider opt-out", () => new MockProviderProductionGuardAssertion(
            new ServiceCollection(), new EmptyRegistry()).StartAsync(default).GetAwaiter().GetResult()),
        ("mock password hasher opt-out", () => new MockPasswordHasherProductionGuardAssertion(
            new ServiceCollection()).StartAsync(default).GetAwaiter().GetResult()),
        ("generic vendor provider selector", () => new ServiceCollection()
            .AddHarborlineVendorProviderSubstrate()
            .UseVendorProviderIfConfigured<TestVendorContract, TestRealVendor>(legacyName)),
    ];

    private static string Diagnostic(string legacyName) =>
        $"Legacy Harborline environment variable {legacyName} is not supported; " +
        $"use {ReplacementPrefix}{legacyName[(legacyName.IndexOf('_') + 1)..]}.";

    public void Dispose()
    {
        foreach (var name in LegacyNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var pair in _savedLegacyEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class EmptyRegistry : IMockVendorEnvVarRegistry
    {
        public IReadOnlyList<(Type ContractType, string EnvVarKey)> Entries => [];
        public void Register(Type contractType, string envVarKey) { }
        public bool TryGet(Type contractType, out string envVarKey)
        {
            envVarKey = string.Empty;
            return false;
        }
    }

    private interface TestVendorContract { }

    private sealed class TestRealVendor : TestVendorContract { }
}
