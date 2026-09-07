using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

using BankingServices = (
    Harborline.Api.Blocks.Banking.Services.IBankAccountRepository AccountRepo,
    Harborline.Api.Blocks.Banking.Services.IStatementLineRepository LineRepo,
    Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository LinkRepo,
    Harborline.Api.Blocks.Banking.Services.IReconciliationRepository ReconciliationRepo,
    Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository PeriodRepo,
    Harborline.Api.Blocks.Banking.Import.ImportPipelineService ImportPipeline,
    Harborline.Api.Blocks.Banking.Matching.AcceptMatchService AcceptMatchService,
    Harborline.Api.Blocks.Banking.Matching.UnMatchService UnMatchService,
    Harborline.Api.Blocks.Banking.Matching.ReconciliationLockLease ReconciliationLease,
    Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider FeedProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.Banking.NodeLocalBankFeedDbContext> FeedConnectionFactory);

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 — the bank-account CREATE now passes a stage-one PERMISSION decision
/// (<c>records:write</c>), not just the device-reachable transport fence. Without the gate a caller who
/// clears the transport fence reaches <c>AccountRepo.AddAsync</c> with no permission decision at all.
/// </summary>
public sealed class BankAccountCreateGateTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private RecordingAccountRepo _accounts = null!;
    private MutableAuthorizationContext _authorization = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _authorization = new MutableAuthorizationContext();
        builder.Services.AddSingleton<IAuthorizationContext>(_authorization);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        _app = builder.Build();

        // Desktop-plane attribution so the device-reachable transport fence admits the request —
        // isolating THIS suite to the permission gate behind it.
        _app.Use(async (context, next) =>
        {
            context.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(context);
        });

        _accounts = new RecordingAccountRepo();
        // Only the create route is exercised — the untouched tuple members are never dereferenced.
        BankingServices banking = (
            _accounts, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        BankAccountRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            banking,
            NodeTestActiveTeam.Accessor,
            new Harborline.Api.LocalNodeHost.Data.Financial.NodeBankAccountWriter(
                _accounts, Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "ticket 151: create without records:write is refused (403), nothing persists")]
    public async Task Create_Without_RecordsWrite_Is_Refused()
    {
        _authorization.Allow(TeamRolePermissions.RecordsRead);

        var resp = await _client.PostAsJsonAsync(BankAccountRoutes.RouteBase, new { displayName = "Ops Checking" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Empty(_accounts.Added);
    }

    [Fact(DisplayName = "ticket 151: create with records:write persists (201)")]
    public async Task Create_With_RecordsWrite_Succeeds()
    {
        _authorization.Allow(TeamRolePermissions.RecordsWrite);

        var resp = await _client.PostAsJsonAsync(BankAccountRoutes.RouteBase, new { displayName = "Ops Checking" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var added = Assert.Single(_accounts.Added);
        Assert.Equal("Ops Checking", added.DisplayName);
    }

    private sealed class RecordingAccountRepo : Harborline.Api.Blocks.Banking.Services.IBankAccountMutationRepository
    {
        public List<BankAccount> Added { get; } = new();

        public Task<BankAccount?> GetByIdAsync(TenantId tenantId, BankAccountId id, CancellationToken ct = default)
            => Task.FromResult<BankAccount?>(null);

        public Task<IReadOnlyList<BankAccount>> ListAsync(TenantId tenantId, bool includeArchived = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BankAccount>>(Added);

        public Task AddAsync(BankAccount account, CancellationToken ct = default)
        {
            Added.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(BankAccount account, CancellationToken ct = default) => Task.CompletedTask;
    }

}
