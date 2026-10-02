using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-980 (DES-0029 ck-2 S2, S3): activation resolves the target's whole dependency closure through
/// <c>KernelPackageClosure</c> against the Active versions, and deactivation refuses while an Active pack
/// depends on the target (D4, no cascade).
/// Packs are seeded straight into the store so a test can hold a state install would refuse.
/// </summary>
public sealed class PackDependencyClosureActivationTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000980");
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPackInstallStore _store = new();
    private readonly InMemoryPackInstallAudit _audit = new();
    private readonly PackInstaller _installer;

    public PackDependencyClosureActivationTests()
    {
        _installer = Installer(_store);
        PlatformPackTestPreload.Activate(_store, Tenant);
    }

    [Fact]
    public void Activate_refuses_while_a_direct_dependency_is_only_installed()
    {
        Seed("test.b", "1.0.0");
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"));

        var outcome = Activate("test.a", "1.0.0");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateDependencyInactive, outcome.Error);
        Assert.Contains("'test.a > test.b'", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(_store.GetActive(Tenant, "test.a"));
    }

    [Fact]
    public void Activate_refuses_while_a_transitive_dependency_is_not_active()
    {
        // B is Active over a C that is only installed: a legacy state install and activation now refuse.
        Seed("test.c", "1.0.0");
        Seed("test.b", "1.0.0", ("test.c", "1.0.0"));
        _store.Activate(Tenant, "test.b", "1.0.0");
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"));

        var outcome = Activate("test.a", "1.0.0");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateDependencyInactive, outcome.Error);
        Assert.Contains("'test.a > test.b > test.c'", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(_store.GetActive(Tenant, "test.a"));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == "test.a" && entry.Detail!.StartsWith(PackInstallCodes.ActivateDependencyInactive, StringComparison.Ordinal));
    }

    [Fact]
    public void Activate_refuses_when_the_active_dependency_is_below_the_pin_even_though_a_newer_one_is_installed()
    {
        Seed("test.b", "1.0.0");
        _store.Activate(Tenant, "test.b", "1.0.0");
        Seed("test.b", "2.0.0");
        Seed("test.a", "1.0.0", ("test.b", "2.0.0"));

        var outcome = Activate("test.a", "1.0.0");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateDependencyBelowPin, outcome.Error);
        Assert.Contains("'test.a > test.b'", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("active 1.0.0 is below the pin 2.0.0", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Activate_proceeds_when_the_whole_closure_is_active_at_or_above_its_pins()
    {
        Seed("test.c", "1.2.0");
        Seed("test.b", "1.0.0", ("test.c", "1.0.0"));
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"), ("test.a", "1.0.0"));

        Assert.True(Activate("test.c", "1.2.0").Activated);
        Assert.True(Activate("test.b", "1.0.0").Activated);
        var outcome = Activate("test.a", "1.0.0");

        Assert.True(outcome.Activated, outcome.Error + " " + outcome.Detail);
    }

    [Fact]
    public void The_closure_is_rechecked_under_the_activation_lease()
    {
        Seed("test.b", "1.0.0");
        _store.Activate(Tenant, "test.b", "1.0.0");
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"));
        // The dependency leaves service after the preliminary checks and before the pointer flip.
        var reader = new DeactivatingReader(_store, "test.a", () => _store.Deactivate(Tenant, "test.b", "1.0.0"));
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()),
            reader, _store, _store, new WorkflowRefusingPackContentAdmission(), _audit,
            Authorization.TestAuthorization.AllowGate());

        var outcome = installer.Activate(Tenant, "test.a", "1.0.0", Now, "operator");

        Assert.Equal(2, reader.TargetReads);
        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateDependencyInactive, outcome.Error);
        Assert.Null(_store.GetActive(Tenant, "test.a"));
    }

    [Fact]
    public void Activate_refuses_while_a_transitive_member_declares_the_inactive_platform_pack()
    {
        var store = new InMemoryPackInstallStore();
        Seed(store, PlatformPackTestPreload.PackKey, "1.0.0");
        Seed(store, "test.b", "1.0.0", (PlatformPackTestPreload.PackKey, "1.0.0"));
        store.Activate(Tenant, "test.b", "1.0.0");
        Seed(store, "test.a", "1.0.0", ("test.b", "1.0.0"));

        var outcome = Installer(store).Activate(Tenant, "test.a", "1.0.0", Now, "operator");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivatePlatformPackRequired, outcome.Error);
        Assert.Contains($"'{PlatformPackTestPreload.PackKey}' must be active before 'test.a'", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(store.GetActive(Tenant, "test.a"));
    }

    [Fact]
    public void A_zero_pin_on_the_inactive_platform_pack_does_not_satisfy_the_platform_root()
    {
        var store = new InMemoryPackInstallStore();
        Seed(store, "test.b", "1.0.0", (PlatformPackTestPreload.PackKey, "0.0.0"));
        store.Activate(Tenant, "test.b", "1.0.0");
        Seed(store, "test.a", "1.0.0", ("test.b", "1.0.0"));

        var outcome = Installer(store).Activate(Tenant, "test.a", "1.0.0", Now, "operator");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivatePlatformPackRequired, outcome.Error);
        Assert.Contains($"'{PlatformPackTestPreload.PackKey}' must be active before 'test.a'", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(store.GetActive(Tenant, "test.a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Activate_refuses_a_non_platform_pack_while_the_platform_pack_is_not_active(bool platformInstalled)
    {
        // D5: the platform pack roots every closure, declared or not.
        var store = new InMemoryPackInstallStore();
        if (platformInstalled) Seed(store, PlatformPackTestPreload.PackKey, "1.0.0");
        Seed(store, "test.a", "1.0.0");

        var outcome = Installer(store).Activate(Tenant, "test.a", "1.0.0", Now, "operator");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivatePlatformPackRequired, outcome.Error);
        Assert.Contains($"'{PlatformPackTestPreload.PackKey}' must be active before 'test.a'", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(store.GetActive(Tenant, "test.a"));
    }

    [Fact]
    public void The_platform_pack_activates_as_its_own_closure_root()
    {
        var store = new InMemoryPackInstallStore();
        Seed(store, PlatformPackTestPreload.PackKey, "2.0.0");

        var outcome = Installer(store).Activate(Tenant, PlatformPackTestPreload.PackKey, "2.0.0", Now, "operator");

        Assert.True(outcome.Activated, outcome.Error + " " + outcome.Detail);
    }

    [Fact]
    public async Task Deactivate_refuses_while_an_active_pack_depends_on_it_and_names_every_dependent()
    {
        Seed("test.c", "1.0.0");
        Seed("test.b", "1.0.0", ("test.c", "1.0.0"));
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"));
        Seed("test.idle", "1.0.0", ("test.c", "1.0.0"));
        Assert.True(Activate("test.c", "1.0.0").Activated);
        Assert.True(Activate("test.b", "1.0.0").Activated);
        Assert.True(Activate("test.a", "1.0.0").Activated);

        var outcome = await _installer.DeactivateAsync(Tenant, "test.c", "1.0.0", Now, "operator");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal(["test.a", "test.b"], outcome.Dependents);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, "test.c")?.Version);
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == "test.c" && entry.Detail == PackInstallCodes.DeactivateDependentsActive + ": test.a, test.b");
    }

    [Fact]
    public async Task Deactivate_proceeds_once_no_active_pack_depends_on_it()
    {
        Seed("test.b", "1.0.0", ("test.b", "1.0.0"));
        Seed("test.a", "1.0.0", ("test.b", "1.0.0"));
        Assert.True(Activate("test.b", "1.0.0").Activated);
        Assert.True(Activate("test.a", "1.0.0").Activated);

        Assert.True((await _installer.DeactivateAsync(Tenant, "test.a", "1.0.0", Now, "operator")).Deactivated);
        var outcome = await _installer.DeactivateAsync(Tenant, "test.b", "1.0.0", Now, "operator");

        Assert.True(outcome.Deactivated, outcome.Error);
        Assert.Null(_store.GetActive(Tenant, "test.b"));
    }

    [Fact]
    public async Task Deactivate_refuses_the_platform_pack_while_a_pack_that_declares_it_is_active()
    {
        Seed("test.a", "1.0.0", (PlatformPackTestPreload.PackKey, "1.0.0"));
        Assert.True(Activate("test.a", "1.0.0").Activated);

        var outcome = await _installer.DeactivateAsync(Tenant, PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version, Now, "operator");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal(["test.a"], outcome.Dependents);
        Assert.NotNull(_store.GetActive(Tenant, PlatformPackTestPreload.PackKey));
    }

    [Fact]
    public async Task Deactivate_refuses_the_platform_pack_while_any_other_pack_is_active_declared_or_not()
    {
        Seed("test.b", "1.0.0");
        Seed("test.a", "1.0.0");
        Assert.True(Activate("test.b", "1.0.0").Activated);
        Assert.True(Activate("test.a", "1.0.0").Activated);

        var outcome = await _installer.DeactivateAsync(Tenant, PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version, Now, "operator");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal(["test.a", "test.b"], outcome.Dependents);
        Assert.NotNull(_store.GetActive(Tenant, PlatformPackTestPreload.PackKey));
    }

    [Fact]
    public async Task Deactivate_of_the_platform_pack_proceeds_when_no_other_pack_is_active()
    {
        var outcome = await _installer.DeactivateAsync(Tenant, PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version, Now, "operator");

        Assert.True(outcome.Deactivated, outcome.Error);
    }

    private PackActivationOutcome Activate(string key, string version)
        => _installer.Activate(Tenant, key, version, Now, "operator");

    private PackInstaller Installer(InMemoryPackInstallStore store) => new(
        new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
        new WorkflowRefusingPackContentAdmission(), _audit, Authorization.TestAuthorization.AllowGate());

    private void Seed(string key, string version, params (string Key, string Version)[] dependencies)
        => Seed(_store, key, version, dependencies);

    private static void Seed(InMemoryPackInstallStore store, string key, string version, params (string Key, string Version)[] dependencies)
        => store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(key, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, [],
                new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                TrustScope.OwnRoster, dependencies.Select(dependency => new PackDependencyRef(dependency.Key, dependency.Version)).ToArray()),
            new PackInstallWatermark(key, version, new Dictionary<string, int>()), []));

    // Runs the hook on the target's second version read: the first is the preliminary check, the second
    // is the authoritative read inside the activation lease.
    private sealed class DeactivatingReader(IPackInstallStore inner, string targetKey, Action hook) : IPackInstallStore
    {
        public int TargetReads { get; private set; }
        public InstalledPack? GetVersion(TenantId tenant, string key, string version)
        {
            if (key == targetKey && ++TargetReads == 2) hook();
            return inner.GetVersion(tenant, key, version);
        }
        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant) => inner.ListInstalled(tenant);
        public InstalledPack? GetActive(TenantId tenant, string key) => inner.GetActive(tenant, key);
        public bool AnyInstalled() => inner.AnyInstalled();
        public PackInstallWatermark? GetWatermark(TenantId tenant, string key) => inner.GetWatermark(tenant, key);
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string key) => inner.GetOverrides(tenant, key);
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => inner.GetKeyOwnership(tenant);
    }
}
