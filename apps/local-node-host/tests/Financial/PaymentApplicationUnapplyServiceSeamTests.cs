using Harborline.Api.Kernel.Runtime.Teams;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using AuthorizationGate = Harborline.Api.Foundation.Authorization.AuthorizationGate;
using AuthorizationWriteContext = Harborline.Api.Foundation.Authorization.AuthorizationWriteContext;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Financial;

/// <summary>
/// Deterministic service-seam tests for payment-application unapply ordering and races.
/// </summary>
public sealed class PaymentApplicationUnapplyServiceSeamTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow =
        DateTimeOffset.Parse("2026-07-16T17:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private string _directory = null!;
    private ServiceProvider _provider = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfPaymentApplicationRepository _inner = null!;
    private InterceptingPaymentApplicationRepository _applications = null!;
    private DefaultPaymentApplicationService _service = null!;
    private readonly RecordingPeriodResolver _periods = new();

    /// <summary>The authority every act in this fixture is decided with and stamped at.</summary>
    private static readonly AuthorizationWriteContext Authority =
        new(new ActorId("user:seam-test-operator"), LocalTenantId, FixedNow);

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-payment-unapply-seam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "payment-unapply-seam.db");
        var services = new ServiceCollection();

        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAp.Data.ApEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPayments.Data.PaymentsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        foreach (var enlistment in NodeJournalWriteAdapters.Create()
                     .Where(candidate => candidate.Invariant != NodeWriteInvariants.IssuedInvoice))
        {
            services.AddSingleton(enlistment);
        }

        services.AddTestActiveTeam();
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddSingleton<NodeEfJournalStore>();
        services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        services.AddNodeFinancialPosting();
        services.AddNodeBillWrites();
        services.AddNodeInvoiceWrites();
        services.AddNodePaymentWrites();

        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _inner = _provider.GetRequiredService<NodeEfPaymentApplicationRepository>();
        _applications = new InterceptingPaymentApplicationRepository(_inner);
        // Holds nothing by default: the caller reaches the soft-close override with no grant, which is
        // the shape every test that is NOT about the override wants (the period is Open there anyway).
        _service = ServiceWith(TestRouteGate.Denying());
    }

    /// <summary>
    /// Builds the service under test over <paramref name="gate"/> — the point-of-use authority the
    /// reversal-date soft-close override is resolved through (ticket 205 slice 5). A null gate is a
    /// container that cannot DECIDE the override.
    /// </summary>
    /// <param name="gate">The gate, or null for an undecidable act.</param>
    private DefaultPaymentApplicationService ServiceWith(AuthorizationGate? gate) =>
        new(
            payments: _provider.GetRequiredService<IPaymentRepository>(),
            applications: _applications,
            invoices: _provider.GetRequiredService<IInvoiceRepository>(),
            bills: _provider.GetRequiredService<IBillRepository>(),
            tenantContext: _provider.GetRequiredService<ITenantContext>(),
            // Open by default; the period-gate tests below re-point _periods before acting.
            periods: _periods,
            gate: gate,
            events: _provider.GetRequiredService<Harborline.Api.Foundation.Events.IDomainEventPublisher>(),
            timeProvider: new FixedTimeProvider(FixedNow));

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            if (_directory is not null && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best effort — mirror PaymentWriteRouteTests teardown.
        }
    }

    [Fact]
    public async Task UnapplyAsync_ReverseReturnsNull_PinsSuccessWithBalancesMutatedAndNoEvidence()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0001",
            paymentId: "PAY-SEAM1",
            journalEntryId: "JE-SEAM1-CLEAR",
            actor: actor);

        _applications.ArmBeforeReverse(async () =>
        {
            await using var ctx = await _factory.CreateDbContextAsync();
            var application = await ctx.Set<PaymentApplication>()
                .SingleAsync(row => row.Id == applicationId);
            ctx.Set<PaymentApplication>().Remove(application);
            await ctx.SaveChangesAsync();
        });

        var result = await _service.UnapplyAsync(applicationId, actor, Authority);

        await using var postState = await _factory.CreateDbContextAsync();
        var invoice = await postState.Set<Invoice>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == new InvoiceId("INV-2026-08-22-SEAM-0001"));
        var payment = await postState.Set<Payment>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == new PaymentId("PAY-SEAM1"));

        // FIXED CONTRACT (was a defect pin for api-financial-10). The reversal is now attempted
        // BEFORE any balance is touched and its nullable result is checked, so a reversal that did
        // not happen refuses the whole operation instead of reporting success over restored
        // balances with zero contra evidence. The invoice keeps its payment and the payment keeps
        // its application, because nothing was written.
        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalNotRecorded, result.Error);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(500m, invoice.AmountPaid);
        Assert.Equal(0m, invoice.Balance);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, payment.UnappliedAmount);
        Assert.Equal(PaymentStatus.Applied, payment.Status);

        // The arming hook hard-deleted the row, so there is no evidence to find — the point is that
        // the balances above were not restored to match a reversal that never happened.
        Assert.Equal(0, await postState.Set<PaymentApplication>().CountAsync());
    }

    [Fact]
    public async Task Unapply_GatesOnTheReversalDate_NotTheApplicationsOwnPeriod()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0003",
            paymentId: "PAY-SEAM3",
            journalEntryId: "JE-SEAM3-CLEAR",
            actor: actor);

        var result = await _service.UnapplyAsync(applicationId, actor, Authority);

        Assert.True(result.Success);
        // THE RULING, asserted rather than described: the resolver is consulted exactly once, and
        // for the REVERSAL date. A gate that also read the application's own period would query a
        // second date here, and would make a locked past period permanently uncorrectable.
        Assert.Equal([DateOnly.FromDateTime(FixedNow.UtcDateTime)], _periods.QueriedDates);
    }

    [Fact]
    public async Task Unapply_WhenTheReversalPeriodIsLocked_RefusesAndRestoresNothing()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0004",
            paymentId: "PAY-SEAM4",
            journalEntryId: "JE-SEAM4-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.Locked;

        var result = await _service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalPeriodLocked, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0004", "PAY-SEAM4");
    }

    [Fact]
    public async Task Unapply_WhenNoPeriodCoversTheReversalDate_Refuses()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0005",
            paymentId: "PAY-SEAM5",
            journalEntryId: "JE-SEAM5-CLEAR",
            actor: actor);
        // No period at all — the contra would land outside every period, so it must not be written.
        _periods.Status = null;

        var result = await _service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.NoPeriodForReversalDate, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0005", "PAY-SEAM5");
    }

    [Fact]
    public async Task Unapply_WhenTheReversalPeriodIsSoftClosed_RefusesWithoutAGrant()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0006",
            paymentId: "PAY-SEAM6",
            journalEntryId: "JE-SEAM6-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.SoftClosed;

        var result = await _service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalPeriodSoftClosed, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0006", "PAY-SEAM6");
    }

    [Fact]
    public async Task Unapply_WhenTheReversalPeriodIsSoftClosed_AllowsAGrantOverThatPeriod()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0007",
            paymentId: "PAY-SEAM7",
            journalEntryId: "JE-SEAM7-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.SoftClosed;
        // Same soft-closed period, one grant different — this pair proves the override is authorized by a
        // holding OVER THE PERIOD rather than by something incidental about the SoftClosed branch.
        var service = ServiceWith(TestRouteGate.ScopedToRecord(
            LocalTenantId, SoftCloseOverrideRecordKind, RecordingPeriodResolver.SeamPeriodId));

        var result = await service.UnapplyAsync(applicationId, actor, Authority);

        Assert.True(result.Success);
        Assert.Equal(UnapplyError.None, result.Error);
    }

    [Fact]
    public async Task Unapply_WhenTheReversalPeriodIsSoftClosed_RefusesAGrantOverADifferentPeriod()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0008",
            paymentId: "PAY-SEAM8",
            journalEntryId: "JE-SEAM8-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.SoftClosed;
        // The caller holds the override — over SOME OTHER period. The record the act addresses is what
        // decides, so the gate's own scope containment refuses (ledger L592/L670).
        var service = ServiceWith(TestRouteGate.ScopedToRecord(
            LocalTenantId, SoftCloseOverrideRecordKind, "period:some-other-quarter"));

        var result = await service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalPeriodSoftClosed, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0008", "PAY-SEAM8");
    }

    [Fact]
    public async Task Unapply_WhenTheReversalPeriodHasNoRecordToAddress_RefusesRatherThanThrowing()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0009",
            paymentId: "PAY-SEAM9",
            journalEntryId: "JE-SEAM9-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.SoftClosed;
        _periods.PeriodId = "   ";
        // `financial:period-override-soft-close` is NOT declared install-wide, so a record-LESS check of it
        // is a bug the gate refuses by throwing. An unnameable record must therefore refuse BEFORE the gate
        // is asked — a fail-closed 'no', never an escaping ArgumentException (slice 4 R2).
        Assert.False(PermissionVocabulary.IsInstallWide(
            AuthorizationOperation.Parse(Permission.FinancialPeriodOverrideSoftClose)));
        var service = ServiceWith(TestRouteGate.AllowAll());

        var result = await service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalPeriodSoftClosed, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0009", "PAY-SEAM9");
    }

    [Fact]
    public async Task Unapply_WhenTheContainerCannotDecideTheOverride_RefusesRatherThanGranting()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0010",
            paymentId: "PAY-SEAM10",
            journalEntryId: "JE-SEAM10-CLEAR",
            actor: actor);
        _periods.Status = IPeriodResolver.Status.SoftClosed;
        // No gate at all — the ERPNext import pipeline's shape. An act nobody can decide does not happen.
        var service = ServiceWith(gate: null);

        var result = await service.UnapplyAsync(applicationId, actor, Authority);

        Assert.False(result.Success);
        Assert.Equal(UnapplyError.ReversalPeriodSoftClosed, result.Error);
        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0010", "PAY-SEAM10");
    }

    [Fact]
    public async Task Unapply_RefusesAnAuthorityForADifferentTenantRatherThanDecidingWithIt()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0011",
            paymentId: "PAY-SEAM11",
            journalEntryId: "JE-SEAM11-CLEAR",
            actor: actor);
        var foreign = Authority with { Tenant = new TenantId("tenant:not-this-one") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UnapplyAsync(applicationId, actor, foreign));

        await AssertNothingUnappliedAsync("INV-2026-08-22-SEAM-0011", "PAY-SEAM11");
    }

    /// <summary>
    /// The record kind the soft-close override addresses, read from the gate's own single reading rather
    /// than spelled a second time here.
    /// </summary>
    private static readonly string SoftCloseOverrideRecordKind = AuthorizationGate.RecordKindFor(
        AuthorizationOperation.Parse(Permission.FinancialPeriodOverrideSoftClose));

    /// <summary>
    /// Asserts a refused unapply left the invoice and payment exactly as the application put them.
    /// </summary>
    /// <param name="invoiceId">The seeded invoice number.</param>
    /// <param name="paymentId">The seeded payment id.</param>
    private async Task AssertNothingUnappliedAsync(string invoiceId, string paymentId)
    {
        await using var state = await _factory.CreateDbContextAsync();
        var invoice = await state.Set<Invoice>().AsNoTracking()
            .SingleAsync(row => row.Id == new InvoiceId(invoiceId));
        var payment = await state.Set<Payment>().AsNoTracking()
            .SingleAsync(row => row.Id == new PaymentId(paymentId));
        var application = await state.Set<PaymentApplication>().AsNoTracking()
            .SingleAsync(row => row.PaymentId == new PaymentId(paymentId));

        Assert.Equal(500m, invoice.AmountPaid);
        Assert.Equal(0m, invoice.Balance);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, payment.UnappliedAmount);
        Assert.Equal(PaymentStatus.Applied, payment.Status);
        Assert.True(application.IsActive);
        Assert.Null(application.ReversedByApplicationId);
    }

    [Fact]
    public async Task UnapplyAsync_ConcurrentUnapplyOfSameApplication_DoubleRestoresPaymentBalance()
    {
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var applicationId = await SeedAppliedInvoicePaymentAsync(
            invoiceId: "INV-2026-08-22-SEAM-0002",
            paymentId: "PAY-SEAM2",
            journalEntryId: "JE-SEAM2-CLEAR",
            actor: actor);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _applications.ArmBeforeReverse(async () =>
        {
            parked.SetResult();
            await release.Task;
        });

        var task1 = _service.UnapplyAsync(applicationId, actor, Authority);
        await parked.Task;
        var result2 = await _service.UnapplyAsync(applicationId, actor, Authority);
        release.SetResult();
        var result1 = await task1;

        await using var postState = await _factory.CreateDbContextAsync();
        var rows = await postState.Set<PaymentApplication>().AsNoTracking().ToListAsync();
        var payment = await postState.Set<Payment>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == new PaymentId("PAY-SEAM2"));
        var invoice = await postState.Set<Invoice>()
            .AsNoTracking()
            .SingleAsync(row => row.Id == new InvoiceId("INV-2026-08-22-SEAM-0002"));

        // Ticket 095: exactly one of the two concurrent unapplies wrote contra evidence, so exactly
        // one reports success. result2 runs to completion while result1 is parked before its
        // ReverseAsync, so result1 is the one that lands on the AlreadyReversed branch.
        Assert.False(result1.Success);
        Assert.Equal(UnapplyError.ReversalNotRecorded, result1.Error);
        Assert.True(result2.Success);
        Assert.Equal(2, await postState.Set<PaymentApplication>().CountAsync());
        Assert.Single(rows, row => row.ReversesApplicationId is not null);

        // The balance corruption is GONE. It used to read 1000m against a 500m payment, because the
        // old order let the second unapply read the first one's restored figure and add to it. With
        // the reversal attempted first, both restores are computed from the same pre-reversal
        // snapshot, so last-write-wins lands on the one correct value.
        Assert.Equal(500m, payment.UnappliedAmount);
        Assert.Equal(PaymentStatus.Unapplied, payment.Status);
        Assert.Equal(0m, invoice.AmountPaid);
        Assert.Equal(500m, invoice.Balance);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);

        // TRIPWIRE (ticket 095, rewritten to the fixed contract): success is reported by exactly the
        // caller that wrote contra evidence. Collapse PaymentApplicationReversalResult back to a
        // nullable PaymentApplication and the AlreadyReversed branch becomes indistinguishable from
        // Recorded, both calls report success again, and this assertion fails.
        Assert.Single(rows, row => row.ReversesApplicationId is not null);
        Assert.True(result1.Success ^ result2.Success,
            "Exactly one concurrent unapply must report success — the one that wrote the contra row.");
    }

    private async Task<PaymentApplicationId> SeedAppliedInvoicePaymentAsync(
        string invoiceId,
        string paymentId,
        string journalEntryId,
        Harborline.Api.Blocks.People.Foundation.Models.PartyId actor)
    {
        await SeedIssuedInvoiceAsync(invoiceId);
        var payment = Payment.Create(
            tenantId: LocalTenantId,
            chartId: new ChartOfAccountsId("CH-1"),
            direction: PaymentDirection.Inbound,
            paymentNumber: paymentId,
            partyId: new Harborline.Api.Blocks.People.Foundation.Models.PartyId("CUST-SEAM"),
            paymentDate: new DateOnly(2026, 1, 15),
            amount: 500m,
            method: PaymentMethod.ACH,
            id: new PaymentId(paymentId),
            bankAccountId: new GLAccountId("1000"),
            createdAtUtc: new Instant(FixedNow)) with
        {
            Status = PaymentStatus.Unapplied,
            JournalEntryId = new JournalEntryId(journalEntryId),
            UpdatedAtUtc = new Instant(FixedNow),
        };
        await _provider.GetRequiredService<NodeEfPaymentRepository>()
            .AddAsync(LocalTenantId, payment, new Instant(System.TimeProvider.System.GetUtcNow()).Value);

        var applied = await _service.ApplyAsync(
            payment.Id,
            AppliedTo.Invoice,
            invoiceId,
            500m,
            0m,
            0m,
            actor);
        Assert.Equal(ApplyError.None, applied.Error);
        return Assert.Single(await _inner.ListByTargetAsync(LocalTenantId, invoiceId)).Id;
    }

    private async Task SeedIssuedInvoiceAsync(string invoiceId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var issueDate = new DateOnly(2026, 1, 1);
        var line = InvoiceLine.Create(
            invoiceId: new InvoiceId(invoiceId),
            lineNumber: 1,
            description: "Seam invoice",
            quantity: 1m,
            unitPrice: 500m,
            incomeAccountId: new GLAccountId("4000"));
        var invoice = Invoice.Create(
            tenantId: LocalTenantId,
            chartId: new ChartOfAccountsId("CH-1"),
            invoiceNumber: invoiceId,
            customerId: new Harborline.Api.Blocks.People.Foundation.Models.PartyId("CUST-SEAM"),
            issueDate: issueDate,
            dueDate: issueDate.AddDays(30),
            lines: new[] { line },
            arAccountId: new GLAccountId("1100"),
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            id: new InvoiceId(invoiceId)) with
        {
            Status = InvoiceStatus.Issued,
        };
        ctx.Set<Invoice>().Add(invoice);
        await ctx.SaveChangesAsync();
    }

    private sealed class InterceptingPaymentApplicationRepository : IPaymentApplicationRepository
    {
        private readonly IPaymentApplicationRepository _inner;
        private Func<Task>? _beforeReverse;

        public InterceptingPaymentApplicationRepository(IPaymentApplicationRepository inner)
        {
            _inner = inner;
        }

        public void ArmBeforeReverse(Func<Task> hook)
        {
            _beforeReverse = hook;
        }

        public Task AddAsync(
            TenantId tenantId,
            PaymentApplication application,
            DateTimeOffset admittedAt,
            CancellationToken cancellationToken = default) =>
            _inner.AddAsync(tenantId, application, admittedAt, cancellationToken);

        public Task<PaymentApplication?> GetAsync(
            TenantId tenantId,
            PaymentApplicationId id,
            DateTimeOffset admittedAt,
            CancellationToken cancellationToken = default) =>
            _inner.GetAsync(tenantId, id, admittedAt, cancellationToken);

        public async Task<PaymentApplicationReversalResult> ReverseAsync(
            TenantId tenantId,
            PaymentApplicationId id,
            Instant reversedAtUtc,
            CancellationToken cancellationToken = default)
        {
            var hook = Interlocked.Exchange(ref _beforeReverse, null);
            if (hook is not null)
            {
                await hook();
            }

            return await _inner.ReverseAsync(tenantId, id, reversedAtUtc, cancellationToken);
        }

        public Task<IReadOnlyList<PaymentApplication>> ListByPaymentAsync(
            TenantId tenantId,
            PaymentId paymentId,
            CancellationToken cancellationToken = default) =>
            _inner.ListByPaymentAsync(tenantId, paymentId, cancellationToken);

        public Task<IReadOnlyList<PaymentApplication>> ListByTargetAsync(
            TenantId tenantId,
            string targetId,
            CancellationToken cancellationToken = default) =>
            _inner.ListByTargetAsync(tenantId, targetId, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// An <see cref="IPeriodResolver"/> whose answer the test controls, and which RECORDS every date
    /// it was asked about. The recording is what proves the gate reads the reversal date — asserting
    /// a refusal alone cannot distinguish "gated on today" from "gated on the application's period".
    /// </summary>
    private sealed class RecordingPeriodResolver : IPeriodResolver
    {
        private readonly List<DateOnly> _queried = [];

        /// <summary>The id of the period this resolver reports.</summary>
        internal const string SeamPeriodId = "period:seam-test";

        /// <summary>The status to report; null reports NO period covering the date.</summary>
        internal IPeriodResolver.Status? Status { get; set; } = IPeriodResolver.Status.Open;

        /// <summary>The period id to report — blank models a period the resolver cannot name.</summary>
        internal string PeriodId { get; set; } = SeamPeriodId;

        internal IReadOnlyList<DateOnly> QueriedDates => _queried;

        public Task<IPeriodResolver.PeriodSnapshot?> ResolveAsync(
            ChartOfAccountsId chartId,
            DateOnly date,
            CancellationToken cancellationToken = default)
        {
            _queried.Add(date);
            return Task.FromResult(Status is { } status
                ? new IPeriodResolver.PeriodSnapshot(PeriodId, chartId.Value, status)
                : (IPeriodResolver.PeriodSnapshot?)null);
        }
    }

}
