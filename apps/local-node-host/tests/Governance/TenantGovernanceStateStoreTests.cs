using System;
using System.IO;
using System.Threading.Tasks;

using Harborline.Api.LocalNodeHost.Data.Governance;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

/// <summary>
/// Tests for the durable, file-backed tenant-governance store (ADR 0144 AD.1 setup-phase mechanics, slice
/// B5): genesis writes setup, genesis is idempotent, the founder-declared setup → operating transition, its
/// idempotency, and durability across store instances (the founder's declared milestone must survive a
/// restart).
/// </summary>
public sealed class TenantGovernanceStateStoreTests : IDisposable
{
    private readonly string _dir;
    private const string Tenant = "tenant-alpha";

    public TenantGovernanceStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b5-governance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private FileTenantGovernanceStateStore NewStore(Func<DateTimeOffset>? clock = null)
        => FileTenantGovernanceStateStore.InDirectory(_dir, clock ?? TimeProvider.System.GetUtcNow);

    [Fact]
    public async Task Genesis_writes_setup_phase()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        using var store = NewStore(() => now);

        var state = await store.EnsureGenesisAsync(Tenant);

        Assert.Equal(SetupPhase.Setup, state.SetupPhase);
        Assert.Equal(Tenant, state.TenantId);
        Assert.Equal(now, state.EnteredSetupAt);
        Assert.Null(state.EnteredOperatingAt);
    }

    [Fact]
    public async Task Get_returns_null_before_genesis()
    {
        using var store = NewStore();
        Assert.Null(await store.GetAsync(Tenant));
    }

    [Fact]
    public async Task EnsureGenesis_is_idempotent_and_preserves_the_first_genesis_stamp()
    {
        var first = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var later = first.AddHours(3);
        var clock = new MutableClock(first);
        using var store = NewStore(clock.Now);

        var a = await store.EnsureGenesisAsync(Tenant);
        clock.Set(later);
        var b = await store.EnsureGenesisAsync(Tenant);

        // A second genesis returns the FIRST state unchanged — never re-stamps, never re-opens setup.
        Assert.Equal(a.EnteredSetupAt, b.EnteredSetupAt);
        Assert.Equal(first, b.EnteredSetupAt);
        Assert.Equal(SetupPhase.Setup, b.SetupPhase);
    }

    [Fact]
    public async Task EnsureGenesis_does_not_reopen_setup_on_an_operating_instance()
    {
        var t0 = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(t0);
        using var store = NewStore(clock.Now);

        await store.EnsureGenesisAsync(Tenant);
        clock.Set(t0.AddMinutes(10));
        await store.FinishSetupAsync(Tenant);

        clock.Set(t0.AddHours(1));
        var afterGenesisAgain = await store.EnsureGenesisAsync(Tenant);

        // Genesis on an already-operating instance must NOT silently re-open setup chrome.
        Assert.Equal(SetupPhase.Operating, afterGenesisAgain.SetupPhase);
    }

    [Fact]
    public async Task FinishSetup_flips_setup_to_operating_and_stamps_the_transition()
    {
        var genesisAt = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var lockedAt = genesisAt.AddMinutes(30);
        var clock = new MutableClock(genesisAt);
        using var store = NewStore(clock.Now);

        await store.EnsureGenesisAsync(Tenant);
        clock.Set(lockedAt);
        var operating = await store.FinishSetupAsync(Tenant);

        Assert.Equal(SetupPhase.Operating, operating.SetupPhase);
        Assert.Equal(genesisAt, operating.EnteredSetupAt);
        Assert.Equal(lockedAt, operating.EnteredOperatingAt);
    }

    [Fact]
    public async Task FinishSetup_is_idempotent_and_the_first_declaration_wins()
    {
        var genesisAt = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var firstLock = genesisAt.AddMinutes(30);
        var secondLock = genesisAt.AddHours(5);
        var clock = new MutableClock(genesisAt);
        using var store = NewStore(clock.Now);

        await store.EnsureGenesisAsync(Tenant);
        clock.Set(firstLock);
        await store.FinishSetupAsync(Tenant);
        clock.Set(secondLock);
        var again = await store.FinishSetupAsync(Tenant);

        Assert.Equal(SetupPhase.Operating, again.SetupPhase);
        // A re-declare is a no-op — the FIRST "start running" instant is preserved, never re-stamped.
        Assert.Equal(firstLock, again.EnteredOperatingAt);
    }

    [Fact]
    public async Task FinishSetup_without_prior_genesis_establishes_then_locks_down()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        using var store = NewStore(() => now);

        // The founder's declaration is never lost to a missing genesis row.
        var operating = await store.FinishSetupAsync(Tenant);

        Assert.Equal(SetupPhase.Operating, operating.SetupPhase);
        Assert.Equal(now, operating.EnteredSetupAt);
        Assert.Equal(now, operating.EnteredOperatingAt);
    }

    [Fact]
    public async Task State_is_durable_across_store_instances()
    {
        var genesisAt = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var lockedAt = genesisAt.AddMinutes(30);

        using (var writer = NewStore(() => genesisAt))
        {
            await writer.EnsureGenesisAsync(Tenant);
        }
        using (var declarer = NewStore(() => lockedAt))
        {
            await declarer.FinishSetupAsync(Tenant);
        }

        // A fresh store instance (a node restart) reads the persisted operating phase — the founder's
        // declared milestone survives, so setup chrome does not re-show.
        using var reader = NewStore(() => lockedAt.AddDays(1));
        var read = await reader.GetAsync(Tenant);

        Assert.NotNull(read);
        Assert.Equal(SetupPhase.Operating, read!.SetupPhase);
        Assert.Equal(genesisAt, read.EnteredSetupAt);
        Assert.Equal(lockedAt, read.EnteredOperatingAt);
    }

    [Fact]
    public async Task Distinct_tenants_have_independent_phases()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        using var store = NewStore(() => now);

        await store.EnsureGenesisAsync("tenant-a");
        await store.EnsureGenesisAsync("tenant-b");
        await store.FinishSetupAsync("tenant-a");

        Assert.Equal(SetupPhase.Operating, (await store.GetAsync("tenant-a"))!.SetupPhase);
        Assert.Equal(SetupPhase.Setup, (await store.GetAsync("tenant-b"))!.SetupPhase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private sealed class MutableClock
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public void Set(DateTimeOffset now) => _now = now;
        public DateTimeOffset Now() => _now;
    }
}
