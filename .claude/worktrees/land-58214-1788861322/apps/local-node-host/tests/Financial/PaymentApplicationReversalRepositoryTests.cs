using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Financial;

public sealed class PaymentApplicationReversalRepositoryTests
{
    private static readonly TenantId Tenant = new("tenant-ppi2");
    private static readonly Instant AppliedAt = new(
        new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
    private static readonly Instant ReversedAt = new(
        new DateTimeOffset(2026, 7, 16, 14, 15, 0, TimeSpan.Zero));
    [Fact]
    public async Task ReverseAsync_SubCentAmounts_RoundTripUnroundedAndContraZeroesPair()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant,
                new PaymentId("payment-cent"),
                AppliedTo.Invoice,
                "invoice-cent",
                amountApplied: 0.005m,
                new DateOnly(2026, 7, 15),
                discountAmount: 0.0025m,
                writeoffAmount: 0.0001m,
                createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);

            var reversed = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
            Assert.Equal(PaymentApplicationReversalOutcome.Recorded, reversed.Outcome);
            var original = await repository.GetAsync(Tenant, application.Id);
            var contra = await repository.GetAsync(
                Tenant,
                Assert.IsType<PaymentApplicationId>(reversed.Application!.ReversedByApplicationId));

            Assert.NotNull(original);
            Assert.NotNull(contra);
            Assert.Equal(0.005m, original!.AmountApplied);
            Assert.Equal(0.0025m, original.DiscountAmount);
            Assert.Equal(0.0001m, original.WriteoffAmount);
            Assert.Equal(-0.005m, contra!.AmountApplied);
            Assert.Equal(-0.0025m, contra.DiscountAmount);
            Assert.Equal(-0.0001m, contra.WriteoffAmount);
            Assert.Equal(0m, original.AmountApplied + contra.AmountApplied);
            Assert.Equal(0m, original.DiscountAmount + contra.DiscountAmount);
            Assert.Equal(0m, original.WriteoffAmount + contra.WriteoffAmount);

            // SQLite's TEXT decimal storage round-trips sub-cent values exactly, so each
            // reversal evidence pair reconstructs to zero off the whole-cent grid;
            // HasPrecision(18, 4) is advisory on this provider.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddAsync_ValueBeyondDeclaredScale_PersistsUnroundedOnSqlite()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant,
                new PaymentId("payment-scale"),
                AppliedTo.Invoice,
                "invoice-scale",
                amountApplied: 0.00005m,
                new DateOnly(2026, 7, 15),
                createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);

            var retrieved = await repository.GetAsync(Tenant, application.Id);
            var reversed = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
            Assert.Equal(PaymentApplicationReversalOutcome.Recorded, reversed.Outcome);
            var original = await repository.GetAsync(Tenant, application.Id);
            var contra = await repository.GetAsync(
                Tenant,
                Assert.IsType<PaymentApplicationId>(reversed.Application!.ReversedByApplicationId));

            Assert.NotNull(retrieved);
            Assert.NotNull(original);
            Assert.NotNull(contra);
            Assert.Equal(0.00005m, retrieved!.AmountApplied);
            Assert.Equal(-0.00005m, contra!.AmountApplied);
            Assert.Equal(0m, original!.AmountApplied + contra.AmountApplied);

            // HONEST PIN: SQLite enforces no scale, so this five-decimal value persists
            // unrounded and its reversal still zeroes. Npgsql numeric(18,4) would round or
            // reject it; that cross-provider divergence belongs with the model-drift tests.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReverseAsync_ThreeWayAllocationSplit_EachPairZeroes()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var applications = new[]
            {
                PaymentApplication.Create(Tenant, new PaymentId("payment-split"), AppliedTo.Invoice,
                    "invoice-split", 33.3333m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-split-1"), createdAtUtc: AppliedAt),
                PaymentApplication.Create(Tenant, new PaymentId("payment-split"), AppliedTo.Invoice,
                    "invoice-split", 33.3333m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-split-2"), createdAtUtc: AppliedAt),
                PaymentApplication.Create(Tenant, new PaymentId("payment-split"), AppliedTo.Invoice,
                    "invoice-split", 33.3334m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-split-3"), createdAtUtc: AppliedAt),
            };

            foreach (var application in applications)
            {
                await repository.AddAsync(Tenant, application);
            }

            var reversed = new List<PaymentApplication>();
            foreach (var application in applications)
            {
                var result = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
                Assert.Equal(PaymentApplicationReversalOutcome.Recorded, result.Outcome);
                reversed.Add(result.Application!);
            }

            var rows = await repository.ListByPaymentAsync(Tenant, new PaymentId("payment-split"));
            await using var ctx = await factory.CreateDbContextAsync();
            var contra1 = await repository.GetAsync(
                Tenant, Assert.IsType<PaymentApplicationId>(reversed[0].ReversedByApplicationId));
            var contra2 = await repository.GetAsync(
                Tenant, Assert.IsType<PaymentApplicationId>(reversed[1].ReversedByApplicationId));
            var contra3 = await repository.GetAsync(
                Tenant, Assert.IsType<PaymentApplicationId>(reversed[2].ReversedByApplicationId));

            Assert.Equal(6, await ctx.Set<PaymentApplication>().CountAsync());
            Assert.Equal(-33.3333m, contra1!.AmountApplied);
            Assert.Equal(-33.3333m, contra2!.AmountApplied);
            Assert.Equal(-33.3334m, contra3!.AmountApplied);
            Assert.Equal(0m, rows.Sum(row => row.AmountApplied));
            Assert.All(rows, row => Assert.False(row.IsActive));

            // At an allocation split, the residual-cent leg and its contra must cancel
            // independently, not merely disappear in the aggregate.
            Assert.Equal(0m, applications[0].AmountApplied + contra1.AmountApplied);
            Assert.Equal(0m, applications[1].AmountApplied + contra2.AmountApplied);
            Assert.Equal(0m, applications[2].AmountApplied + contra3.AmountApplied);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReverseAsync_PersistsLinkedOriginalAndContraAcrossRestart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        var application = PaymentApplication.Create(
            Tenant,
            new PaymentId("payment-ppi2"),
            AppliedTo.Invoice,
            "invoice-ppi2",
            125m,
            new DateOnly(2026, 7, 15),
            createdAtUtc: AppliedAt);
        PaymentApplicationId reversalId;

        try
        {
            await using (var provider = await NewProviderAsync(databasePath))
            {
                var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
                var repository = new NodeEfPaymentApplicationRepository(factory);
                await repository.AddAsync(Tenant, application);

                var reversed = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);

                Assert.Equal(PaymentApplicationReversalOutcome.Recorded, reversed.Outcome);
                Assert.Equal(ReversedAt, reversed.Application!.ReversedAtUtc);
                reversalId = Assert.IsType<PaymentApplicationId>(reversed.Application.ReversedByApplicationId);
            }

            SqliteConnection.ClearAllPools();

            await using (var reopened = await NewProviderAsync(databasePath))
            {
                var factory = reopened.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
                var repository = new NodeEfPaymentApplicationRepository(factory);

                var retained = await repository.GetAsync(Tenant, application.Id);
                var contra = await repository.GetAsync(Tenant, reversalId);

                Assert.NotNull(retained);
                Assert.Equal(ReversedAt, retained!.ReversedAtUtc);
                Assert.Equal(reversalId, retained.ReversedByApplicationId);
                Assert.NotNull(contra);
                Assert.Equal(application.Id, contra!.ReversesApplicationId);
                Assert.Equal(-application.AmountApplied, contra.AmountApplied);
                Assert.Equal(ReversedAt, contra.CreatedAtUtc);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    [Fact]
    public async Task ReverseAsync_UnknownId_ReturnsNullAndWritesNothing()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);

            var result = await repository.ReverseAsync(
                Tenant, new PaymentApplicationId("app-missing"), ReversedAt);
            await using var ctx = await factory.CreateDbContextAsync();

            Assert.Equal(PaymentApplicationReversalOutcome.NotFound, result.Outcome);
            Assert.Null(result.Application);
            Assert.Equal(0, await ctx.Set<PaymentApplication>().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReverseAsync_ForeignTenantId_ReturnsNullAndLeavesOriginalActive()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);

            var result = await repository.ReverseAsync(
                new TenantId("tenant-intruder"), application.Id, ReversedAt);
            var retained = await repository.GetAsync(Tenant, application.Id);
            await using var ctx = await factory.CreateDbContextAsync();

            Assert.Equal(PaymentApplicationReversalOutcome.NotFound, result.Outcome);
            Assert.NotNull(retained);
            Assert.Null(retained!.ReversedAtUtc);
            Assert.Null(retained.ReversedByApplicationId);
            Assert.Equal(1, await ctx.Set<PaymentApplication>().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReverseAsync_OnContraRow_ReturnsNullAndWritesNothing()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);
            var reversed = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
            Assert.Equal(PaymentApplicationReversalOutcome.Recorded, reversed.Outcome);
            var contraId = Assert.IsType<PaymentApplicationId>(reversed.Application!.ReversedByApplicationId);

            var result = await repository.ReverseAsync(
                Tenant,
                contraId,
                new Instant(new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero)));
            var contra = await repository.GetAsync(Tenant, contraId);
            await using var ctx = await factory.CreateDbContextAsync();

            Assert.Equal(PaymentApplicationReversalOutcome.NotFound, result.Outcome);
            Assert.Equal(2, await ctx.Set<PaymentApplication>().CountAsync());
            Assert.NotNull(contra);
            Assert.Null(contra!.ReversedAtUtc);
            Assert.Null(contra.ReversedByApplicationId);
            Assert.Equal(application.Id, contra.ReversesApplicationId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddAsync_TenantMismatch_ThrowsArgumentExceptionAndPersistsNothing()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.AddAsync(new TenantId("tenant-intruder"), application));
            await using var ctx = await factory.CreateDbContextAsync();

            Assert.Equal("application", ex.ParamName);
            Assert.StartsWith(
                $"PaymentApplication '{application.Id.Value}' carries TenantId 'tenant-ppi2' but caller passed tenantId 'tenant-intruder'.",
                ex.Message);
            Assert.Equal(0, await ctx.Set<PaymentApplication>().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddAsync_HandBuiltNegativeUnlinkedApplication_PersistsToday()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant,
                new PaymentId("payment-neg"),
                AppliedTo.Invoice,
                "invoice-neg",
                amountApplied: -125m,
                new DateOnly(2026, 7, 15),
                createdAtUtc: AppliedAt);

            await repository.AddAsync(Tenant, application);
            var retrieved = await repository.GetAsync(Tenant, application.Id);

            Assert.NotNull(retrieved);
            Assert.Equal(-125m, retrieved!.AmountApplied);
            Assert.Null(retrieved.ReversesApplicationId);
            Assert.True(retrieved.IsActive);

            // HONEST PIN of current behavior, not an endorsement. The repository is
            // storage-only by contract, and PaymentApplication.Create has no sign/shape
            // validation, so an unlinked credit row—distinguishable from a contra only by
            // null linkage—persists freely. Rejecting it requires a production design
            // decision; if AddAsync gains shape checks, update this pin deliberately.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<ServiceProvider> NewProviderAsync(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, PaymentsEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return provider;
    }
    [Fact]
    public async Task ReverseAsync_RepeatCall_ReturnsExistingAndInsertsNoSecondContra()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);
            var first = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
            Assert.Equal(PaymentApplicationReversalOutcome.Recorded, first.Outcome);
            var firstContraId = Assert.IsType<PaymentApplicationId>(first.Application!.ReversedByApplicationId);

            var second = await repository.ReverseAsync(
                Tenant,
                application.Id,
                new Instant(new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.Zero)));
            await using var ctx = await factory.CreateDbContextAsync();

            // Ticket 095: a repeat call is AlreadyReversed, not Recorded — it wrote nothing.
            Assert.Equal(PaymentApplicationReversalOutcome.AlreadyReversed, second.Outcome);
            Assert.False(second.WroteEvidence);
            Assert.Equal(ReversedAt, second.Application!.ReversedAtUtc);
            Assert.Equal(firstContraId, second.Application.ReversedByApplicationId);
            Assert.Equal(2, await ctx.Set<PaymentApplication>().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UniqueIndex_SecondContraForSameOriginal_ThrowsOnSqlite()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);
            var reversed = await repository.ReverseAsync(Tenant, application.Id, ReversedAt);
            Assert.Equal(PaymentApplicationReversalOutcome.Recorded, reversed.Outcome);
            var dup = PaymentApplication.Create(
                Tenant,
                new PaymentId("payment-ppi2"),
                AppliedTo.Invoice,
                "invoice-ppi2",
                amountApplied: -125m,
                new DateOnly(2026, 7, 16),
                createdAtUtc: ReversedAt) with
            {
                ReversesApplicationId = application.Id,
            };

            await using (var ctx = await factory.CreateDbContextAsync())
            {
                ctx.Set<PaymentApplication>().Add(dup);
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
                var sqlite = Assert.IsType<SqliteException>(ex.InnerException);
                Assert.Equal(19, sqlite.SqliteErrorCode);
                // SQLite's UNIQUE-violation message names the indexed COLUMNS, not the index
                // (index names appear only for expression indexes). The (TenantId,
                // ReversesApplicationId) pair identifies ux_payment_applications_tenant_reverses
                // uniquely — it is the only unique index on those columns.
                Assert.Contains(
                    "payment_applications.TenantId, payment_applications.ReversesApplicationId",
                    sqlite.Message);
            }

            await using var postState = await factory.CreateDbContextAsync();
            Assert.Equal(2, await postState.Set<PaymentApplication>().CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UniqueIndex_NullReverses_IsNullDistinct_AllowsManyActiveRows()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var applications = new[]
            {
                PaymentApplication.Create(Tenant, new PaymentId("payment-nd"), AppliedTo.Invoice,
                    "invoice-nd", 10m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-nd-1"), createdAtUtc: AppliedAt),
                PaymentApplication.Create(Tenant, new PaymentId("payment-nd"), AppliedTo.Invoice,
                    "invoice-nd", 20m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-nd-2"), createdAtUtc: AppliedAt),
                PaymentApplication.Create(Tenant, new PaymentId("payment-nd"), AppliedTo.Invoice,
                    "invoice-nd", 30m, new DateOnly(2026, 7, 15),
                    id: new PaymentApplicationId("app-nd-3"), createdAtUtc: AppliedAt),
            };

            foreach (var application in applications)
            {
                await repository.AddAsync(Tenant, application);
            }

            await using var ctx = await factory.CreateDbContextAsync();
            Assert.Equal(3, await ctx.Set<PaymentApplication>().CountAsync());

            // With the PostgreSQL-only filter stripped, active rows coexist because
            // SQLite treats NULLs as distinct in a unique index. A provider/model change
            // to NULLS NOT DISTINCT must fail this test loudly.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReverseAsync_ConcurrentCalls_NeverProduceTwoContras()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ppi2-payment-application-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "local-node.db");

        try
        {
            await using var provider = await NewProviderAsync(databasePath);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var repository = new NodeEfPaymentApplicationRepository(factory);
            var application = PaymentApplication.Create(
                Tenant, new PaymentId("payment-ppi2"), AppliedTo.Invoice, "invoice-ppi2",
                125m, new DateOnly(2026, 7, 15), createdAtUtc: AppliedAt);
            await repository.AddAsync(Tenant, application);

            var task1 = Task.Run(() => repository.ReverseAsync(Tenant, application.Id, ReversedAt));
            var task2 = Task.Run(() => repository.ReverseAsync(Tenant, application.Id, ReversedAt));
            PaymentApplicationReversalResult? result1 = null;
            PaymentApplicationReversalResult? result2 = null;
            Exception? error1 = null;
            Exception? error2 = null;
            try
            {
                result1 = await task1;
            }
            catch (Exception ex)
            {
                error1 = ex;
            }
            try
            {
                result2 = await task2;
            }
            catch (Exception ex)
            {
                error2 = ex;
            }

            await using var ctx = await factory.CreateDbContextAsync();
            var rows = await ctx.Set<PaymentApplication>().AsNoTracking().ToListAsync();
            var contra = Assert.Single(rows, row => row.ReversesApplicationId is not null);
            var original = Assert.Single(rows, row => row.Id == application.Id);

            Assert.Equal(2, await ctx.Set<PaymentApplication>().CountAsync());
            Assert.Equal(contra.Id, original.ReversedByApplicationId);
            // Ticket 095: whichever calls returned, exactly one of them can claim it wrote evidence.
            Assert.True(result1 is not null || result2 is not null);
            Assert.Equal(
                1,
                new[] { result1, result2 }.Count(r => r is { WroteEvidence: true }));
            if (error1 is not null)
            {
                Assert.True(error1 is DbUpdateException or SqliteException);
            }
            if (error2 is not null)
            {
                Assert.True(error2 is DbUpdateException or SqliteException);
            }

            // T10 deterministically proves the index backstop. This real-interleaving smoke
            // pins only the must-never-double-insert invariant and tolerates the SQLite
            // busy/serialized-write outcomes of the currently unlocked EF implementation.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal static class PaymentApplicationRepositoryActInstantExtensions
{
    private static readonly DateTimeOffset AdmittedAt =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    internal static Task AddAsync(
        this NodeEfPaymentApplicationRepository repository,
        TenantId tenantId,
        PaymentApplication application,
        CancellationToken cancellationToken = default)
        => repository.AddAsync(tenantId, application, AdmittedAt, cancellationToken);

    internal static Task<PaymentApplication?> GetAsync(
        this NodeEfPaymentApplicationRepository repository,
        TenantId tenantId,
        PaymentApplicationId id,
        CancellationToken cancellationToken = default)
        => repository.GetAsync(tenantId, id, AdmittedAt, cancellationToken);
}
