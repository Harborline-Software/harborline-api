using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Persistence-level proof that reconciliation lock transitions are compare-and-swap writes.
/// </summary>
public sealed class ReconciliationLockConcurrencyTests
{
    [Fact]
    public async Task TwoLockAttempts_FromSameSnapshot_PersistExactlyOneHolder()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-reconciliation-lock-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHarborlineEntityModule, BankingEntityModule>();
            services.AddDbContextFactory<LocalNodeDbContext>(options =>
                options.UseSqlite(
                    $"Data Source={Path.Combine(directory, "local-node.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();

            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
                var version = context.Model
                    .FindEntityType(typeof(Reconciliation))!
                    .FindProperty(nameof(Reconciliation.Version))!;
                Assert.True(version.IsConcurrencyToken);
            }

            var repository = new NodeEfReconciliationRepository(factory);
            var now = new Instant(new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero));
            var tenant = new TenantId("tenant-lock-concurrency");
            var account = BankAccountId.NewId();
            var reconciliation = new Reconciliation(
                ReconciliationId.NewId(),
                tenant,
                account,
                new FiscalPeriodId("period-lock-concurrency"),
                OpeningBalance: 0m,
                StatementClosingBalance: 0m,
                ClearedMovement: 0m,
                LockState: BankReconciliationLockState.Open,
                LockedAt: null,
                LockedByPrincipalId: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now);
            await repository.AddAsync(reconciliation);

            // Both callers fetched version zero before either wrote. Serializing the two writes
            // keeps the test deterministic while preserving the exact stale-snapshot race.
            var snapshotA = await repository.GetByIdAsync(tenant, reconciliation.Id);
            var snapshotB = await repository.GetByIdAsync(tenant, reconciliation.Id);
            Assert.NotNull(snapshotA);
            Assert.NotNull(snapshotB);

            var attemptA = snapshotA! with
            {
                LockState = BankReconciliationLockState.Locked,
                LockedAt = now,
                LockedByPrincipalId = "member-a",
                UpdatedAtUtc = now,
                Version = snapshotA.Version + 1,
            };
            var attemptB = snapshotB! with
            {
                LockState = BankReconciliationLockState.Locked,
                LockedAt = now,
                LockedByPrincipalId = "member-b",
                UpdatedAtUtc = now,
                Version = snapshotB.Version + 1,
            };

            var wonA = await repository.UpdateAsync(attemptA);
            var wonB = await repository.UpdateAsync(attemptB);

            Assert.True(wonA);
            Assert.False(wonB);
            var stored = await repository.GetByIdAsync(tenant, reconciliation.Id);
            Assert.NotNull(stored);
            Assert.Equal("member-a", stored!.LockedByPrincipalId);
            Assert.Equal(1, stored.Version);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadContract_ReportsTheLeaseAndNotTheStoredColumn()
    {
        // 3421 — the wedge as the HUMAN experiences it. Every mutation guard now asks the lease, so a
        // read that serialized the raw column would tell the client "Locked" about a reconciliation
        // the server would hand straight over, leaving the UI stuck on a lock nobody holds.
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));
        var lease = new ReconciliationLockLease(clock, TimeSpan.FromMinutes(15));
        var lockedAt = new Instant(clock.GetUtcNow());

        var held = Rec(BankReconciliationLockState.Locked, lockedAt);
        Assert.Equal("Locked", BankAccountRoutes.ProjectEffectiveLockState(held, lease));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("Open", BankAccountRoutes.ProjectEffectiveLockState(held, lease));

        var noTimestamp = Rec(BankReconciliationLockState.Locked, null);
        Assert.Equal("Open", BankAccountRoutes.ProjectEffectiveLockState(noTimestamp, lease));

        // A row that was never locked reports Open too. Named by the third review as the one input the
        // inventory did not state explicitly; it is the branch a reader is most likely to assume.
        var neverLocked = Rec(BankReconciliationLockState.Open, null);
        Assert.Equal("Open", BankAccountRoutes.ProjectEffectiveLockState(neverLocked, lease));

        Assert.Equal("Unlocked", BankAccountRoutes.ProjectEffectiveLockState(null, lease));
    }

    private static Reconciliation Rec(BankReconciliationLockState state, Instant? lockedAt) => new(
        ReconciliationId.NewId(),
        new TenantId(Guid.NewGuid().ToString("D")),
        new BankAccountId(Guid.NewGuid().ToString("D")),
        new FiscalPeriodId("period-read-contract"),
        OpeningBalance: 0m,
        StatementClosingBalance: 0m,
        ClearedMovement: 0m,
        LockState: state,
        LockedAt: lockedAt,
        LockedByPrincipalId: "holder",
        CreatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
        UpdatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

}
