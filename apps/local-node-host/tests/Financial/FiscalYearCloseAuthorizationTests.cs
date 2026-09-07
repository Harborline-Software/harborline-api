using NSubstitute;

using System.Globalization;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Events;

namespace Harborline.Api.LocalNodeHost.Tests.Financial;

public sealed class FiscalYearCloseAuthorizationTests
{
    private static readonly DateTimeOffset AuthorityAt =
        new(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResampledAt = AuthorityAt.AddHours(7);
    private static readonly TenantId Tenant = new("tenant-fiscal-year-act");
    private static readonly AuthorizationWriteContext Authority =
        new(new ActorId("financial-admin"), Tenant, AuthorityAt);

    [Fact]
    public async Task CloseFiscalYear_PersistsAndPublishesOnlyTheAuthorityInstant()
    {
        var years = new InMemoryFiscalYearRepository();
        var periods = new InMemoryFiscalPeriodRepository();
        var charts = new InMemoryChartRepository();
        var accounts = new InMemoryAccountTypeQuery();
        var balances = new InMemoryBalanceComputer();
        var events = new RecordingEventPublisher();
        var posting = new RecordingPostingService();
        var ids = await SeedOpenYearAsync(years, periods, charts, accounts, balances);
        var periodClose = new PeriodCloseService(
            periods, years, events, new FixedTimeProvider(ResampledAt), Tenant);
        var service = new FiscalYearCloseService(
            years, periods, periodClose, charts, accounts, balances, posting, events,
            TestAuthorization.AllowGate(), Tenant);

        var result = await service.CloseFiscalYearAsync(ids.YearId, Authority);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuthorityAt, posting.Entry!.CreatedAtUtc.Value);
        var persistedYear = await years.GetAsync(ids.YearId);
        Assert.Equal(AuthorityAt, persistedYear!.ClosedAtUtc!.Value.Value);
        var persistedPeriod = await periods.GetAsync(ids.PeriodId);
        Assert.Equal(AuthorityAt, persistedPeriod!.SoftClosedAtUtc!.Value.Value);
        Assert.Equal(AuthorityAt, persistedPeriod.LockedAtUtc!.Value.Value);
        Assert.Equal(4, events.Envelopes.Count);
        Assert.All(events.Envelopes, envelope => Assert.Equal(AuthorityAt, envelope.OccurredAt));
        Assert.All(events.Envelopes, envelope => Assert.Equal(AuthorityAt, UuidVersion7Timestamp(envelope.EventId)));
    }

    [Fact]
    public async Task ReopenFiscalYear_PersistsAndPublishesOnlyTheAuthorityInstant()
    {
        var years = new InMemoryFiscalYearRepository();
        var periods = new InMemoryFiscalPeriodRepository();
        var events = new RecordingEventPublisher();
        var chartId = new ChartOfAccountsId("chart-reopen");
        var yearId = new FiscalYearId("fy-reopen");
        var periodId = new FiscalPeriodId("period-reopen");
        await years.InsertAsync(new FiscalYear(
            yearId, chartId, "FY reopen", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            FiscalYearStatus.Closed, new Instant(AuthorityAt.AddDays(-1)), null,
            new Instant(AuthorityAt.AddYears(-1)), Version: 1));
        await periods.InsertAsync(new FiscalPeriod(
            periodId, chartId, yearId, FiscalPeriodKind.Annual, "FY reopen",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FiscalPeriodStatus.Locked,
            new Instant(AuthorityAt.AddDays(-2)), new Instant(AuthorityAt.AddDays(-1)), null,
            new Instant(AuthorityAt.AddYears(-1)), Version: 2));
        var periodClose = new PeriodCloseService(
            periods, years, events, new FixedTimeProvider(ResampledAt), Tenant);
        var service = new FiscalYearCloseService(
            years, periods, periodClose, Substitute.For<IChartRepository>(),
            Substitute.For<IAccountTypeQuery>(), Substitute.For<IBalanceComputer>(),
            Substitute.For<IJournalPostingService>(), events, TestAuthorization.AllowGate(), Tenant);

        var result = await service.ReopenFiscalYearAsync(yearId, "approved reopen", Authority);

        Assert.True(result.IsSuccess);
        var persistedPeriod = await periods.GetAsync(periodId);
        Assert.Equal(FiscalPeriodStatus.SoftClosed, persistedPeriod!.Status);
        Assert.Equal(AuthorityAt, persistedPeriod.SoftClosedAtUtc!.Value.Value);
        Assert.Null(persistedPeriod.LockedAtUtc);
        Assert.Equal(2, events.Envelopes.Count);
        Assert.All(events.Envelopes, envelope => Assert.Equal(AuthorityAt, envelope.OccurredAt));
        Assert.All(events.Envelopes, envelope => Assert.Equal(AuthorityAt, UuidVersion7Timestamp(envelope.EventId)));
    }

    [Fact]
    public async Task ReopenFiscalYear_DenialPrecedesEveryStoreWrite()
    {
        var years = Substitute.For<IFiscalYearRepository>();
        var periods = Substitute.For<IFiscalPeriodRepository>();
        var periodClose = Substitute.For<IPeriodCloseService>();
        var events = new RecordingEventPublisher();
        var requests = new List<AuthorizationGateRequest>();
        var service = new FiscalYearCloseService(
            years, periods, periodClose, Substitute.For<IChartRepository>(),
            Substitute.For<IAccountTypeQuery>(), Substitute.For<IBalanceComputer>(),
            Substitute.For<IJournalPostingService>(), events,
            TestAuthorization.Gate(false, requests.Add), Tenant);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            service.ReopenFiscalYearAsync(new FiscalYearId("fy-denied"), "denied", Authority));

        var request = Assert.Single(requests);
        Assert.Equal("financial-period:override-soft-close", request.Act.Operation.Value);
        Assert.Equal("financial-period", request.Target.RecordKind);
        Assert.Equal("fy-denied", request.Target.RecordId);
        Assert.DoesNotContain(years.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(IFiscalYearRepository.UpdateAsync));
        Assert.DoesNotContain(periods.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(IFiscalPeriodRepository.UpdateAsync));
        Assert.Empty(periodClose.ReceivedCalls());
        Assert.Empty(events.Envelopes);
    }

    private static async Task<(FiscalYearId YearId, FiscalPeriodId PeriodId)> SeedOpenYearAsync(
        InMemoryFiscalYearRepository years,
        InMemoryFiscalPeriodRepository periods,
        InMemoryChartRepository charts,
        InMemoryAccountTypeQuery accounts,
        InMemoryBalanceComputer balances)
    {
        var chartId = new ChartOfAccountsId("chart-close");
        var yearId = new FiscalYearId("fy-close");
        var periodId = new FiscalPeriodId("period-close");
        var revenueId = new GLAccountId("revenue");
        var retainedId = new GLAccountId("retained-earnings");
        var createdAt = new Instant(AuthorityAt.AddYears(-1));
        charts.Upsert(new ChartOfAccounts(
            chartId, new LegalEntityId("entity"), "Main", "USD", 1, 1, retainedId,
            true, createdAt, createdAt));
        accounts.Upsert(new GLAccount(
            revenueId, "4000", "Revenue", GLAccountType.Revenue, ChartId: chartId));
        balances.Seed(revenueId, -125m);
        await years.InsertAsync(FiscalYear.CreateOpen(
            yearId, chartId, "FY close", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), createdAt));
        await periods.InsertAsync(FiscalPeriod.CreateOpen(
            periodId, chartId, yearId, FiscalPeriodKind.Annual, "FY close",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), createdAt));
        return (yearId, periodId);
    }

    private sealed class RecordingPostingService : IJournalPostingService
    {
        internal JournalEntry? Entry { get; private set; }

        public Task<PostResult> PostAsync(
            JournalEntry entry,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default)
        {
            Entry = entry;
            return Task.FromResult(new PostResult(entry, PostError.None, null));
        }
    }

    private sealed class RecordingEventPublisher : IDomainEventPublisher
    {
        internal List<(string EventType, string EventId, DateTimeOffset OccurredAt)> Envelopes { get; } = [];

        public Task PublishAsync<TPayload>(
            DomainEventEnvelope<TPayload> envelope,
            CancellationToken cancellationToken = default)
        {
            Envelopes.Add((envelope.EventType, envelope.EventId, envelope.OccurredAt));
            return Task.CompletedTask;
        }
    }

    private static DateTimeOffset UuidVersion7Timestamp(string eventId)
    {
        var canonicalHex = Guid.Parse(eventId).ToString("N");
        var unixMilliseconds = long.Parse(
            canonicalHex.AsSpan(0, 12),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
