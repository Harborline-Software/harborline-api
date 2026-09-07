using Harborline.Api.Foundation.Coordination;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Coordination;

public sealed class WriteEnlistmentTests
{
    private static readonly WriteInvariant HomeEpoch = new("home-epoch");
    private static readonly WriteInvariant IssuedInvoice = new("issued-invoice");

    [Fact]
    public async Task DeclaredRequirementWithoutHandler_RefusesSave()
    {
        var operation = new DeclaredWriteOperation("invoice.issue", [IssuedInvoice]);
        var unitOfWork = new RecordingUnitOfWork();
        var registry = new WriteEnlistmentRegistry([], HomeEpoch);

        var exception = await Assert.ThrowsAsync<MissingWriteEnlistmentException>(async () =>
        {
            await registry.EnlistAsync(operation, unitOfWork);
            await unitOfWork.SaveAsync();
        });

        Assert.Equal(IssuedInvoice, exception.Invariant);
        Assert.False(unitOfWork.WasSaved);
    }

    [Fact]
    public async Task Registry_RunsFenceFirst_WithoutPriorities()
    {
        var calls = new List<string>();
        var audit = new WriteInvariant("audit");
        var operation = new DeclaredWriteOperation(
            "journal.post",
            [audit, HomeEpoch, IssuedInvoice]);
        var registry = new WriteEnlistmentRegistry(
            [
                new RecordingEnlistment(audit, calls),
                new RecordingEnlistment(IssuedInvoice, calls),
                new RecordingEnlistment(HomeEpoch, calls),
            ],
            HomeEpoch);

        await registry.EnlistAsync(operation, new RecordingUnitOfWork());

        Assert.Equal(["home-epoch", "audit", "issued-invoice"], calls);
        Assert.DoesNotContain(
            typeof(IWriteEnlistment).GetProperties(),
            property => property.Name.Contains("Priority", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HomeEpoch_RegisteredButOutOfScope_IsNotApplicable_WhileMissingIsRefused()
    {
        await using var context = Context();
        var entry = BalancedEntry();
        var unitOfWork = new NodeJournalWriteUnitOfWork(context, entry,
            TestAuthorization.AllowedDecision(entry.TenantId, entry.Id.Value,
                "journal-entry", Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.LedgerPost));
        var operation = new DeclaredWriteOperation("journal.post", [NodeWriteInvariants.HomeEpoch]);
        var registered = new WriteEnlistmentRegistry([new HomeEpochFenceEnlister()], NodeWriteInvariants.HomeEpoch);

        var outcomes = await registered.EnlistAsync(operation, unitOfWork);

        Assert.Equal(WriteEnlistmentOutcome.NotApplicable, outcomes[NodeWriteInvariants.HomeEpoch]);
        var missing = new WriteEnlistmentRegistry([], NodeWriteInvariants.HomeEpoch);
        await Assert.ThrowsAsync<MissingWriteEnlistmentException>(
            () => missing.EnlistAsync(operation, unitOfWork));
    }

    [Fact]
    public async Task IssuedInvoice_RegisteredButOutOfScope_IsNotApplicable_WhileMissingIsRefused()
    {
        await using var context = Context();
        var entry = BalancedEntry();
        var unitOfWork = new NodeJournalWriteUnitOfWork(context, entry,
            TestAuthorization.AllowedDecision(entry.TenantId, entry.Id.Value,
                "journal-entry", Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.LedgerPost));
        var operation = new DeclaredWriteOperation("journal.post", [NodeWriteInvariants.IssuedInvoice]);
        var registered = new WriteEnlistmentRegistry(
            [new NodeIssuedInvoiceWriteEnlister()],
            NodeWriteInvariants.HomeEpoch);

        var outcomes = await registered.EnlistAsync(operation, unitOfWork);

        Assert.Equal(WriteEnlistmentOutcome.NotApplicable, outcomes[NodeWriteInvariants.IssuedInvoice]);
        var missing = new WriteEnlistmentRegistry([], NodeWriteInvariants.HomeEpoch);
        await Assert.ThrowsAsync<MissingWriteEnlistmentException>(
            () => missing.EnlistAsync(operation, unitOfWork));
    }

    [Fact]
    public void FourAdapters_ImplementTheDomainFreePlatformSeam()
    {
        Type[] adapters =
        [
            typeof(HomeEpochFenceEnlister),
            typeof(NodeAuditWriteEnlister),
            typeof(NodeRecurringInvoiceWriteEnlister),
            typeof(NodeIssuedInvoiceWriteEnlister),
        ];

        Assert.All(adapters, adapter => Assert.True(typeof(IWriteEnlistment).IsAssignableFrom(adapter)));

        var method = typeof(IWriteEnlistment).GetMethod(nameof(IWriteEnlistment.EnlistAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(StagedWriteUnitOfWork), method!.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(JournalEntry));
    }

    [Fact]
    public async Task JournalChokepoint_MissingDeclaredHandler_DoesNotPersistEntry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var store = new NodeEfJournalStore(factory, Array.Empty<IWriteEnlistment>());

        await Assert.ThrowsAsync<MissingWriteEnlistmentException>(
            () => store.SaveAtomicForTestAsync(new TenantId("tenant-1"), BalancedEntry()));

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Set<JournalEntry>().CountAsync());
    }

    [Fact]
    public void JournalChokepoint_HasOneRegistryConstructor_NotFourNullableSlots()
    {
        var constructor = Assert.Single(typeof(NodeEfJournalStore).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IEnumerable<IWriteEnlistment>), parameters[1].ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.HasDefaultValue);
    }

    [Fact]
    public void IssuedScope_HasNoPostCommitFallbackSignal()
    {
        Assert.Null(typeof(PendingIssuedInvoiceUpdate).GetProperty("WasConsumed"));
        Assert.Null(typeof(PendingIssuedInvoiceUpdate).GetMethod("MarkConsumed"));
    }

    private static LocalNodeDbContext Context()
    {
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new LocalNodeDbContext(options, []);
    }

    private static JournalEntry BalancedEntry() =>
        new(
            new JournalEntryId("JE-ENLISTMENT"),
            new TenantId("tenant-1"),
            new DateOnly(2026, 8, 18),
            "coordination test",
            [
                new JournalEntryLine(new GLAccountId("1000"), 10m, 0m),
                new JournalEntryLine(new GLAccountId("4000"), 0m, 10m),
            ],
            new Instant(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero)));

    private sealed class RecordingUnitOfWork : StagedWriteUnitOfWork
    {
        public bool WasSaved { get; private set; }

        public Task SaveAsync()
        {
            WasSaved = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEnlistment(
        WriteInvariant invariant,
        ICollection<string> calls) : IWriteEnlistment
    {
        public WriteInvariant Invariant { get; } = invariant;

        public ValueTask<WriteEnlistmentOutcome> EnlistAsync(
            StagedWriteUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            calls.Add(Invariant.Name);
            return ValueTask.FromResult(WriteEnlistmentOutcome.Enlisted);
        }
    }
}
